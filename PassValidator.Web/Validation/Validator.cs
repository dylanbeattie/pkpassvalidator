﻿using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace PassValidator.Web.Validation
{
    public class Validator
    {
        private string[] validWWDRCertificateSerialNumbers = new[] {"01DEBCC4396DA010"};
        public ValidationResult Validate(byte[] passContent)
        {
            ValidationResult result = new ValidationResult();

            string passTypeIdentifier = null;
            string teamIdentifier = null;
            string signaturePassTypeIdentifier = null;
            string signatureTeamIdentifier = null;
            byte[] manifestFile = null;
            byte[] signatureFile = null;

            using (MemoryStream zipToOpen = new MemoryStream(passContent))
            {
                using (ZipArchive archive = new ZipArchive(zipToOpen, ZipArchiveMode.Read, false))
                {
                    foreach (var e in archive.Entries)
                    {
                        if (e.FullName.ToLower().Equals("manifest.json"))
                        {
                            result.HasManifest = true;

                            using (var stream = e.Open())
                            {
                                using (var ms = new MemoryStream())
                                {
                                    stream.CopyTo(ms);
                                    ms.Position = 0;
                                    manifestFile = ms.ToArray();
                                }
                            }
                        }

                        if (e.FullName.ToLower().Equals("pass.json"))
                        {
                            result.HasPass = true;

                            using (var stream = e.Open())
                            {
                                using (var ms = new MemoryStream())
                                {
                                    stream.CopyTo(ms);
                                    ms.Position = 0;
                                    var file = ms.ToArray();

                                    var jsonObject = JObject.Parse(Encoding.UTF8.GetString(file));

                                    passTypeIdentifier = GetKeyStringValue(jsonObject, "passTypeIdentifier");
                                    result.HasPassTypeIdentifier = !string.IsNullOrWhiteSpace(passTypeIdentifier);

                                    teamIdentifier = GetKeyStringValue(jsonObject, "teamIdentifier");
                                    result.HasTeamIdentifier = !string.IsNullOrWhiteSpace(teamIdentifier);

                                    var description = GetKeyStringValue(jsonObject, "description");
                                    result.HasDescription = !string.IsNullOrWhiteSpace(description);

                                    if (jsonObject.ContainsKey("formatVersion"))
                                    {
                                        var formatVersion = jsonObject["formatVersion"].Value<int>();
                                        result.HasFormatVersion = formatVersion == 1;
                                    }

                                    var serialNumber = GetKeyStringValue(jsonObject, "serialNumber");
                                    result.HasSerialNumber = !string.IsNullOrWhiteSpace(serialNumber);

                                    if (result.HasSerialNumber)
                                    {
                                        result.hasSerialNumberOfCorrectLength = serialNumber.Length >= 16;
                                    }

                                    var organizationName = GetKeyStringValue(jsonObject, "organizationName");
                                    result.HasOrganizationName = !string.IsNullOrWhiteSpace(organizationName);

                                    if (jsonObject.ContainsKey("appLaunchURL"))
                                    {
                                        result.HasAppLaunchUrl = true;
                                        result.HasAssociatedStoreIdentifiers = jsonObject.ContainsKey("associatedStoreIdentifiers");
                                    }

                                    if (jsonObject.ContainsKey("webServiceURL"))
                                    {
                                        result.HasWebServiceUrl = true;

                                        var webServiceUrl = GetKeyStringValue(jsonObject, "webServiceURL");
                                        result.WebServiceUrlIsHttps = webServiceUrl.ToLower().StartsWith("https://");
                                    }

                                    if (jsonObject.ContainsKey("authenticationToken"))
                                    {
                                        result.HasAuthenticationToken = true;

                                        var authToken = GetKeyStringValue(jsonObject, "authenticationToken");
                                        result.AuthenticationTokenCorrectLength = authToken.Length >= 16;
                                    }

                                    if (result.HasAuthenticationToken && !result.HasWebServiceUrl)
                                    {
                                        result.AuthenticationTokenRequiresWebServiceUrl = true;
                                    }

                                    if (result.HasWebServiceUrl && !result.HasAuthenticationToken)
                                    {
                                        result.WebServiceUrlRequiresAuthenticationToken = true;
                                    }
                                }
                            }

                        }

                        if (e.FullName.ToLower().Equals("signature"))
                        {
                            result.HasSignature = true;

                            using (var stream = e.Open())
                            {
                                using (var ms = new MemoryStream())
                                {
                                    stream.CopyTo(ms);
                                    ms.Position = 0;
                                    signatureFile = ms.ToArray();
                                }
                            }
                        }

                        if (e.FullName.ToLower().Equals("icon.png"))
                        {
                            result.HasIcon1x = true;
                        }

                        if (e.FullName.ToLower().Equals("icon@2x.png"))
                        {
                            result.HasIcon2x = true;
                        }

                        if (e.FullName.ToLower().Equals("icon@3x.png"))
                        {
                            result.HasIcon3x = true;
                        }
                    }
                }
            }

            if (result.HasManifest)
            {
                ContentInfo contentInfo = new ContentInfo(manifestFile);
                SignedCms signedCms = new SignedCms(contentInfo, true);

                signedCms.Decode(signatureFile);

                try
                {
                    signedCms.CheckSignature(true);
                }
                catch
                {

                }

                var signer = signedCms.SignerInfos[0];

                var wwdrCertSubject = "CN=Apple Worldwide Developer Relations Certification Authority, OU=Apple Worldwide Developer Relations, O=Apple Inc., C=US";

                // There are two certificates attached. One is the PassType certificate. One is the WWDR certificate.
                //
                X509Certificate2 appleWWDRCertificate = null;
                X509Certificate2 passKitCertificate = null;

                foreach (var cert in signedCms.Certificates)
                {
                    if (cert.IssuerName.Name.Contains("OU=Apple Certification Authority"))
                    {
                        // Assume this is a valid WWDR certificate; we validate the version separately based on serial #
                        appleWWDRCertificate = cert;
                    }
                    else if (cert.IssuerName.Name == "CN=Apple Worldwide Developer Relations Certification Authority, OU=Apple Worldwide Developer Relations, O=Apple Inc., C=US")
                    {
                        passKitCertificate = cert;
                    }
                }

                if (passKitCertificate != null)
                {
                    result.PassKitCertificateFound = true;

                    foreach (var extension in passKitCertificate.Extensions)
                    {
                        // 1.2.840.113635.100.6.1.16 is the OID of the problematic part I think.
                        // This is an Apple custom extension (1.2.840.113635.100.6.1.16) and in good passes, 
                        // the value matches the pass type identifier.
                        //
                        if (extension.Oid.Value == "1.2.840.113635.100.6.1.16")
                        {
                            var value = Encoding.ASCII.GetString(extension.RawData);
                            value = value.Substring(2, value.Length - 2);

                            result.PassKitCertificateNameCorrect = value == passTypeIdentifier;
                        }
                    }

                    result.PassKitCertificateExpired = passKitCertificate.NotAfter < DateTime.UtcNow;
                }

                if (appleWWDRCertificate is null)
                {
                    result.SignedByApple = false;
                }
                else 
                {
                    result.WWDRCertificateExpired = appleWWDRCertificate.NotAfter < DateTime.UtcNow;
                    result.WWDRCertificateSubjectMatches = appleWWDRCertificate.Subject == wwdrCertSubject;

                    result.SignedByApple = signer.Certificate.IssuerName.Name == wwdrCertSubject;

                    result.WWDRCertificateIsCorrectVersion =
                        validWWDRCertificateSerialNumbers.Contains(appleWWDRCertificate.SerialNumber);

                    if (result.SignedByApple)
                    {
                        var cnValues = Parse(signer.Certificate.Subject, "CN");
                        var ouValues = Parse(signer.Certificate.Subject, "OU");

                        var passTypeIdentifierSubject = cnValues[0];
                        signaturePassTypeIdentifier = passTypeIdentifierSubject.Replace("Pass Type ID: ", "");

                        if (ouValues != null && ouValues.Count > 0)
                        {
                            signatureTeamIdentifier = ouValues[0];
                        }

                        result.HasSignatureExpired = signer.Certificate.NotAfter < DateTime.UtcNow;
                        result.SignatureExpirationDate = signer.Certificate.NotAfter.ToString("yyyy-MM-dd HH:mm:ss");
                    }
                }

                result.PassTypeIdentifierMatches = passTypeIdentifier == signaturePassTypeIdentifier;
                result.TeamIdentifierMatches = teamIdentifier == signatureTeamIdentifier;
            }

            return result;
        }

        private string GetKeyStringValue(JObject jsonObject, string key)
        {
            return jsonObject.ContainsKey(key) ? jsonObject[key].Value<string>() : null;
        }

        public static List<string> Parse(string data, string delimiter)
        {
            if (data == null) return null;
            if (!delimiter.EndsWith("=")) delimiter = delimiter + "=";
            if (!data.Contains(delimiter)) return null;
            //base case
            var result = new List<string>();
            int start = data.IndexOf(delimiter) + delimiter.Length;
            int length = data.IndexOf(',', start) - start;
            if (length == 0) return null; //the group is empty
            if (length > 0)
            {
                result.Add(data.Substring(start, length));
                //only need to recurse when the comma was found, because there could be more groups
                var rec = Parse(data.Substring(start + length), delimiter);
                if (rec != null) result.AddRange(rec); //can't pass null into AddRange() :(
            }
            else //no comma found after current group so just use the whole remaining string
            {
                result.Add(data.Substring(start));
            }
            return result;
        }
    }
}
