﻿// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portionas of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

namespace Microsoft.Management.Powershell.PFXImport.Cmdlets
{
    using System;
    using System.Management.Automation;
    using System.IO;
    using System.Security.Cryptography.X509Certificates;
    using System.Globalization;
    using System.Security;
    using System.Runtime.InteropServices;
    using System.Security.Cryptography;
    using Services.Api;
    using Intune.EncryptionUtilities;
    using static Intune.EncryptionUtilities.CNGNCryptInterop;

    [Cmdlet(VerbsCommon.New, "IntuneUserPfxCertificate", DefaultParameterSetName = SinglePFXFile)]
    [OutputType(typeof(UserPFXCertificate))]
    public class NewUserPFXCertificate : PSCmdlet
    {
        private const string SinglePFXFile = "SinglePFXFile";

        // TODO: Add in functionality to optionally pass in a list of PFX Files (Is it normal to use the same password for all files?)
        private const string MultiplePFXFile = "MultiplePFXFile";

        private const string PFXBase64String = "Base64EncodedPfx";

        private const int ErrorCodeCantOpenFile = -2146885623;

        private const int ErrorCodeNetworkPasswordIncorrect = -2147024810;

        [Parameter(Position = 1, Mandatory = true, ParameterSetName = SinglePFXFile)]
        public string PathToPfxFile { get; set; }

        [Parameter(Position = 1, Mandatory = true, ParameterSetName = PFXBase64String)]
        public string Base64EncodedPfx { get; set; }

        [Parameter(Position = 2, Mandatory = true)]
        public SecureString PfxPassword { get; set; }

        [Parameter(Position = 3, Mandatory = true)]
        public string UPN { get; set; }

        [Parameter(Position = 4)]
        public string ProviderName { get; set; }

        [Parameter(Position = 5)]
        public string KeyName { get; set; }

        [Parameter(Position = 6)]
        public UserPfxIntendedPurpose? IntendedPurpose { get; set; } = UserPfxIntendedPurpose.Unassigned;

        [Parameter(Position = 7)]
        public UserPfxPaddingScheme? PaddingScheme { get; set; } = UserPfxPaddingScheme.OaepSha512;

        protected override void ProcessRecord()
        {
            byte[] pfxData;
            if (this.ParameterSetName == PFXBase64String)
            {
                pfxData = Convert.FromBase64String(Base64EncodedPfx);
            }
            else
            {
                pfxData = File.ReadAllBytes(PathToPfxFile);
            }

            X509Certificate2 pfxCert = new X509Certificate2();
            try
            {
                pfxCert.Import(pfxData, PfxPassword, X509KeyStorageFlags.DefaultKeySet);
            }
            catch (CryptographicException ex)
            {
                if (ex.HResult == ErrorCodeCantOpenFile)
                {
                    ThrowTerminatingError(
                        new ErrorRecord(
                            new ArgumentException(
                                string.Format("Could not Read Thumbprint on file at path: '{0}'. File must be a certificate.", PathToPfxFile), ex),
                            Guid.NewGuid().ToString(),
                            ErrorCategory.InvalidArgument,
                            null));
                }
                else if (ex.HResult == ErrorCodeNetworkPasswordIncorrect)
                {
                    ThrowTerminatingError(
                        new ErrorRecord(
                            new ArgumentException("Could not Read Thumbprint. Verify Password is Correct.", ex),
                            Guid.NewGuid().ToString(),
                            ErrorCategory.InvalidArgument,
                            null));
                }
                else
                {
                    ThrowTerminatingError(
                        new ErrorRecord(
                            new ArgumentException("Could not Read Thumbprint. Unknown Cause", ex),
                            Guid.NewGuid().ToString(),
                            ErrorCategory.InvalidArgument,
                            null));
                }
            }

            ManagedRSAEncryption encryptUtility = new ManagedRSAEncryption();
            byte[] password = new byte[PfxPassword.Length];
            GCHandle pinnedPasswordHandle = GCHandle.Alloc(password, GCHandleType.Pinned);
            byte[] encryptedPassword = null;
            try
            {
                ConvertSecureStringToByteArray(PfxPassword, ref password);

                string hashAlgorithm;
                int paddingFlags;

                switch (PaddingScheme)
                {
                    case UserPfxPaddingScheme.Pkcs1:
                        hashAlgorithm = null;
                        paddingFlags = PaddingFlags.PKCS1Padding;
                        break;
                    case UserPfxPaddingScheme.OaepSha1:
                        hashAlgorithm = PaddingHashAlgorithmNames.SHA1;
                        paddingFlags = PaddingFlags.OAEPPadding;
                        break;
                    case UserPfxPaddingScheme.OaepSha256:
                        hashAlgorithm = PaddingHashAlgorithmNames.SHA256;
                        paddingFlags = PaddingFlags.OAEPPadding;
                        break;
                    case UserPfxPaddingScheme.OaepSha384:
                        hashAlgorithm = PaddingHashAlgorithmNames.SHA384;
                        paddingFlags = PaddingFlags.OAEPPadding;
                        break;
                    case UserPfxPaddingScheme.None:
                        PaddingScheme = UserPfxPaddingScheme.OaepSha512;
                        goto default;   // Since C# doesn't allow switch-case fall-through!
                    case UserPfxPaddingScheme.OaepSha512:
                    default:
                        hashAlgorithm = PaddingHashAlgorithmNames.SHA512;
                        paddingFlags = PaddingFlags.OAEPPadding;
                        break;
                }

                encryptedPassword = encryptUtility.EncryptWithLocalKey(ProviderName, KeyName, password, hashAlgorithm, paddingFlags);
            }
            finally
            {
                if(password != null)
                {
                    password.ZeroFill();
                }

                if (pinnedPasswordHandle.IsAllocated)
                {
                    pinnedPasswordHandle.Free();
                }
            }

            string encryptedPasswordString = Convert.ToBase64String(encryptedPassword);

            UserPFXCertificate userPfxCertifiate = new UserPFXCertificate();
            userPfxCertifiate.Thumbprint = pfxCert.Thumbprint;
            userPfxCertifiate.IntendedPurpose = (UserPfxIntendedPurpose)IntendedPurpose;
            userPfxCertifiate.PaddingScheme = (UserPfxPaddingScheme)PaddingScheme;
            userPfxCertifiate.KeyName = KeyName;
            userPfxCertifiate.UserPrincipalName = UPN;
            userPfxCertifiate.ProviderName = ProviderName;
            userPfxCertifiate.StartDateTime = Convert.ToDateTime(pfxCert.GetEffectiveDateString(), CultureInfo.CurrentCulture);
            userPfxCertifiate.ExpirationDateTime = Convert.ToDateTime(pfxCert.GetExpirationDateString(), CultureInfo.CurrentCulture);
            userPfxCertifiate.CreatedDateTime = DateTime.Now;
            userPfxCertifiate.LastModifiedDateTime = DateTime.Now;
            userPfxCertifiate.EncryptedPfxPassword = encryptedPasswordString;
            userPfxCertifiate.EncryptedPfxBlob = pfxData;

            WriteObject(userPfxCertifiate);
        }

        protected override void BeginProcessing()
        {
            ValidateParameters();
        }

        private void ValidateParameters()
        {
            const string ProviderNameVariable = "EncryptPFXFilesProviderName";
            const string KeyNameVariable = "EncryptPFXFilesKeyName";
            const string IntendedPurposeVariable = "EncryptPFXFilesIntendedPurpose";
            const string PaddingSchemeVariable = "EncryptPFXFilesPaddingScheme";

            // Get Session Variable for ProviderName if one wasn't supplied. Throw error if never supplied.
            if (!string.IsNullOrEmpty(ProviderName))
            {
                SessionState.PSVariable.Set(ProviderNameVariable, ProviderName);
            }
            else
            {
                ProviderName = SessionState.PSVariable.GetValue(ProviderNameVariable, string.Empty).ToString();
                if (string.IsNullOrEmpty(ProviderName))
                {
                    ThrowParameterError("ProviderName");
                }
            }

            // Get Session Variable for KeyName if one wasn't supplied. Throw error if never supplied.
            if (!string.IsNullOrEmpty(KeyName))
            {
                SessionState.PSVariable.Set(KeyNameVariable, KeyName);
            }
            else
            {
                KeyName = SessionState.PSVariable.GetValue(KeyNameVariable, string.Empty).ToString();
                if (string.IsNullOrEmpty(KeyName))
                {
                    ThrowParameterError("KeyName");
                }
            }

            // Get Session Variable for IntendedPurpose if one wasn't supplied. Default to Unassigned if never supplied.
            if (IntendedPurpose != null)
            {
                SessionState.PSVariable.Set(IntendedPurposeVariable, IntendedPurpose);
            }
            else
            {
                IntendedPurpose = (UserPfxIntendedPurpose)SessionState.PSVariable.GetValue(IntendedPurposeVariable, 0);
            }

            // Get Session Variable for Padding Scheme if one wasn't supplied. Default to None if never supplied.

            if (PaddingScheme != null)
            {
                SessionState.PSVariable.Set(PaddingSchemeVariable, PaddingScheme);
            }
            else
            {
                PaddingScheme = (UserPfxPaddingScheme)SessionState.PSVariable.GetValue(PaddingSchemeVariable, 0);
            }
        }

        private void ThrowParameterError(string parameterName)
        {
            ThrowTerminatingError(
                new ErrorRecord(
                    new ArgumentException(string.Format(
                        "Must specify '{0}'", parameterName)),
                    Guid.NewGuid().ToString(),
                    ErrorCategory.InvalidArgument,
                    null));
        }

        private void ConvertSecureStringToByteArray(SecureString secureString, ref byte[] output)
        {
            if(secureString == null)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new ArgumentNullException(nameof(secureString)),
                        Guid.NewGuid().ToString(),
                        ErrorCategory.InvalidArgument,
                        null));
            }

            if(output == null)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new ArgumentNullException(nameof(output)),
                        Guid.NewGuid().ToString(),
                        ErrorCategory.InvalidArgument,
                        null));
            }

            if(secureString.Length != output.Length)
            {
                ThrowTerminatingError(
                    new ErrorRecord(
                        new ArgumentException(string.Format("Size {0} of {1} does not match size {2} of {3}", secureString.Length, nameof(secureString), output.Length, nameof(output))),
                        Guid.NewGuid().ToString(),
                        ErrorCategory.InvalidArgument,
                        null));
            }

            IntPtr secretCharPtr = IntPtr.Zero;
            char[] secretCharArray = new char[secureString.Length];

            try
            {
                secretCharPtr = Marshal.SecureStringToGlobalAllocUnicode(secureString);
                Marshal.Copy(secretCharPtr, secretCharArray, 0, secretCharArray.Length);
                for (int i = 0; i < secureString.Length; i+=1)
                {
                    output[i] = (byte)secretCharArray[i];
                }
            }
            finally
            {
                Array.Clear(secretCharArray, 0, secretCharArray.Length);
                Marshal.ZeroFreeGlobalAllocUnicode(secretCharPtr);
            }
        }
    }
}