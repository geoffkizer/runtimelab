// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Quic.Implementations.Managed.Internal
{
    internal enum OpenSslEncryptionLevel
    {
        Initial = 0,
        EarlyData,
        Handshake,
        Application
    }
}
