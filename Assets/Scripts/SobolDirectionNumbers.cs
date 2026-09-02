using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace PathTracing.Sampling
{
    public sealed class SobolDirectionNumbers : IDisposable
    {
        // Parameters from Stephen Joe and Frances Kuo's new-joe-kuo-6.21201 data set.
        // https://web.maths.unsw.edu.au/~fkuo/sobol/
        public const int BitCount = 32;
        public const int MaximumDimensions = 2568;

        private ComputeBuffer _buffer;
        private static uint[] _cachedDirections;

        public ComputeBuffer Buffer
        {
            get
            {
                EnsureBuffer();
                return _buffer;
            }
        }

        private void EnsureBuffer()
        {
            if (_buffer != null)
            {
                return;
            }

            uint[] directions = _cachedDirections ??= BuildDirectionNumbers();
            _buffer = new ComputeBuffer(directions.Length, sizeof(uint), ComputeBufferType.Structured);
            _buffer.SetData(directions);
        }

        public static uint[] BuildDirectionNumbers()
        {
            TextAsset parameterAsset = Resources.Load<TextAsset>("SobolJoeKuoParameters");
            if (parameterAsset == null)
            {
                throw new InvalidOperationException("Missing Resources/SobolJoeKuoParameters.txt.");
            }

            return BuildDirectionNumbers(parameterAsset.text);
        }

        public static uint[] BuildDirectionNumbers(string encodedParameters)
        {
            byte[] compressedParameters = Convert.FromBase64String(encodedParameters);
            byte[] packedParameters;
            using (var compressedStream = new MemoryStream(compressedParameters))
            using (var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
            using (var unpackedStream = new MemoryStream())
            {
                gzipStream.CopyTo(unpackedStream);
                packedParameters = unpackedStream.ToArray();
            }
            var directions = new uint[MaximumDimensions * BitCount];
            for (int bit = 0; bit < BitCount; bit++)
            {
                directions[bit] = 1u << (31 - bit);
            }

            int offset = 2;
            int rowCount = packedParameters[0] | (packedParameters[1] << 8);
            if (rowCount < MaximumDimensions - 1)
            {
                throw new InvalidOperationException("The embedded Joe-Kuo table does not cover the configured Sobol dimensions.");
            }

            for (int dimension = 1; dimension < MaximumDimensions; dimension++)
            {
                int degree = packedParameters[offset++];
                uint polynomial = ReadUInt32(packedParameters, ref offset);
                int directionOffset = dimension * BitCount;
                for (int bit = 0; bit < degree; bit++)
                {
                    uint initialValue = ReadUInt16(packedParameters, ref offset);
                    directions[directionOffset + bit] = initialValue << (31 - bit);
                }

                for (int bit = degree; bit < BitCount; bit++)
                {
                    uint direction = directions[directionOffset + bit - degree];
                    direction ^= direction >> degree;
                    for (int coefficient = 1; coefficient < degree; coefficient++)
                    {
                        if (((polynomial >> (degree - 1 - coefficient)) & 1u) != 0u)
                        {
                            direction ^= directions[directionOffset + bit - coefficient];
                        }
                    }
                    directions[directionOffset + bit] = direction;
                }
            }

            return directions;
        }

        private static ushort ReadUInt16(byte[] data, ref int offset)
        {
            ushort value = (ushort)(data[offset] | (data[offset + 1] << 8));
            offset += 2;
            return value;
        }

        private static uint ReadUInt32(byte[] data, ref int offset)
        {
            uint value = (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));
            offset += 4;
            return value;
        }

        public void Dispose()
        {
            _buffer?.Release();
            _buffer = null;
        }
    }
}
