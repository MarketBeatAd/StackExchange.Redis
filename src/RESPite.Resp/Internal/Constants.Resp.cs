﻿using System;

namespace RESPite.Internal;

internal static partial class Constants
{
    public static readonly ushort CrLfUInt16 = BitConverter.IsLittleEndian ? (ushort)0x0A0D : (ushort)0x0D0A; // see: ASCII

    public static ReadOnlySpan<byte> CrlfBytes => "\r\n"u8;

    public const int MaxRawBytesInt32 = 10,
        MaxProtocolBytesIntegerInt32 = MaxRawBytesInt32 + 3, // ?X10X\r\n where ? could be $, *, etc - usually a length prefix
        MaxProtocolBytesBulkStringInt32 = MaxRawBytesInt32 + 7; // $10\r\nX10X\r\n
    /*
                    MaxBytesInt64 = 26, // $19\r\nX19X\r\n
                    MaxBytesSingle = 27; // $NN\r\nX...X\r\n - note G17 format, allow 20 for payload
    */
}
