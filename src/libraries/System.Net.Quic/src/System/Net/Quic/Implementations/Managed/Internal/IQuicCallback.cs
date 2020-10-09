// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Quic.Implementations.Managed.Internal.OpenSsl;

namespace System.Net.Quic.Implementations.Managed.Internal
{
    internal interface IQuicCallback
    {
        int SetEncryptionSecrets(OpenSslEncryptionLevel level, byte[] readSecret, byte[] writeSecret);
        int AddHandshakeData(OpenSslEncryptionLevel level, byte[] data);
        int Flush();
        int SendAlert(OpenSslEncryptionLevel level, TlsAlert alert);
    }
}
