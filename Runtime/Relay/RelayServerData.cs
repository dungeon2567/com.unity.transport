using System;
using System.Linq;
using System.Net;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Networking.Transport.Logging;

#if RELAY_SDK_INSTALLED
using Unity.Services.Relay.Models;
#endif

namespace Unity.Networking.Transport.Relay
{
    public unsafe struct RelayServerData
    {
        public NetworkEndpoint Endpoint;
        public ushort Nonce;
        public RelayConnectionData ConnectionData;
        public RelayConnectionData HostConnectionData;
        public RelayAllocationId AllocationId;
        public RelayHMACKey HMACKey;

        public readonly byte IsSecure;

        // TODO Should be computed on connection binding (but not Burst compatible today).
        internal fixed byte HMAC[32];

        // String representation of the host as provided to the constructor. For IP addresses this
        // serves no purpose at all, but for hostnames it can be useful to keep it around (since we
        // would otherwise lose it after resolving it). For example, this is used for WebSockets.
        internal FixedString512Bytes HostString;

        // Common code of all byte array-based constructors.
        private RelayServerData(byte[] allocationId, byte[] connectionData, byte[] hostConnectionData, byte[] key)
        {
            Nonce = 0;
            AllocationId = RelayAllocationId.FromByteArray(allocationId);
            ConnectionData = RelayConnectionData.FromByteArray(connectionData);
            HostConnectionData = RelayConnectionData.FromByteArray(hostConnectionData);
            HMACKey = RelayHMACKey.FromByteArray(key);

            // Assign temporary values to those. Chained constructors will set them.
            Endpoint = default;
            IsSecure = 0;

            HostString = default;

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref ConnectionData, ref HMACKey);
            }
        }

#if RELAY_SDK_INSTALLED
        /// <summary>Create a new Relay server data structure from an allocation.</summary>
        /// <param name="allocation">Allocation from which to create the server data.</param>
        /// <param name="connectionType">Type of connection to use ("udp", "dtls", "ws", or "wss").</param>
        public RelayServerData(Allocation allocation, string connectionType)
            : this(allocation.AllocationIdBytes, allocation.ConnectionData, allocation.ConnectionData, allocation.Key)
        {
            // We check against a hardcoded list of strings instead of just trying to find the
            // connection type in the endpoints since it may contains things we don't support
            // (e.g. they provide a "tcp" endpoint which we don't support).
            var supportedConnectionTypes = new string[] { "udp", "dtls", "ws", "wss" };
            if (!supportedConnectionTypes.Contains(connectionType))
                throw new ArgumentException($"Invalid connection type: {connectionType}. Must be udp, dtls, ws, or wss.");

            var serverEndpoint = allocation.ServerEndpoints.First(ep => ep.ConnectionType == connectionType);

            Endpoint = HostToEndpoint(serverEndpoint.Host, (ushort)serverEndpoint.Port);
            IsSecure = serverEndpoint.Secure ? (byte)1 : (byte)0;
            HostString = serverEndpoint.Host;
        }

        /// <summary>Create a new Relay server data structure from a join allocation.</summary>
        /// <param name="allocation">Allocation from which to create the server data.</param>
        /// <param name="connectionType">Type of connection to use ("udp", "dtls", "ws", or "wss").</param>
        public RelayServerData(JoinAllocation allocation, string connectionType)
            : this(allocation.AllocationIdBytes, allocation.ConnectionData, allocation.HostConnectionData, allocation.Key)
        {
            // We check against a hardcoded list of strings instead of just trying to find the
            // connection type in the endpoints since it may contains things we don't support
            // (e.g. they provide a "tcp" endpoint which we don't support).
            var supportedConnectionTypes = new string[] { "udp", "dtls", "ws", "wss" };
            if (!supportedConnectionTypes.Contains(connectionType))
                throw new ArgumentException($"Invalid connection type: {connectionType}. Must be udp, dtls, ws, or wss.");

            var serverEndpoint = allocation.ServerEndpoints.First(ep => ep.ConnectionType == connectionType);

            Endpoint = HostToEndpoint(serverEndpoint.Host, (ushort)serverEndpoint.Port);
            IsSecure = serverEndpoint.Secure ? (byte)1 : (byte)0;
            HostString = serverEndpoint.Host;
        }

#endif

        /// <summary>Create a new Relay server data structure.</summary>
        /// <remarks>
        /// If a hostname is provided as the "host" parameter, this constructor will perform a DNS
        /// resolution to map it to an IP address. If the hostname is not in the OS cache, this
        /// operation can possibly block for a long time (between 20 and 120 milliseconds). If this
        /// is a concern, perform the DNS resolution asynchronously and pass in the resulting IP
        /// address directly (see <see cref="System.Net.Dns.GetHostEntryAsync"/>).
        /// </remarks>
        /// <param name="host">IP address or hostname of the Relay server.</param>
        /// <param name="port">Port of the Relay server.</param>
        /// <param name="allocationId">ID of the Relay allocation.</param>
        /// <param name="connectionData">Connection data of the allocation.</param>
        /// <param name="hostConnectionData">Connection data of the host (same as previous for hosts).</param>
        /// <param name="key">HMAC signature of the allocation.</param>
        /// <param name="isSecure">Whether the Relay connection is to be secured or not.</param>
        public RelayServerData(string host, ushort port, byte[] allocationId, byte[] connectionData,
                               byte[] hostConnectionData, byte[] key, bool isSecure)
            : this(allocationId, connectionData, hostConnectionData, key)
        {
            Endpoint = HostToEndpoint(host, port);
            IsSecure = isSecure ? (byte)1 : (byte)0;
            HostString = host;
        }

        /// <summary>Create a new Relay server data structure (low level constructor).</summary>
        /// <param name="endpoint">Endpoint of the Relay server.</param>
        /// <param name="nonce">Nonce used in connection handshake (preferably random).</param>
        /// <param name="allocationId">ID of the Relay allocation.</param>
        /// <param name="connectionData">Connection data of the allocation.</param>
        /// <param name="hostConnectionData">Connection data of the host (use default for hosts).</param>
        /// <param name="key">HMAC signature of the allocation.</param>
        /// <param name="isSecure">Whether the Relay connection is to be secured or not.</param>
        public RelayServerData(ref NetworkEndpoint endpoint, ushort nonce, ref RelayAllocationId allocationId,
                               ref RelayConnectionData connectionData, ref RelayConnectionData hostConnectionData, ref RelayHMACKey key, bool isSecure)
        {
            Endpoint = endpoint;
            Nonce = nonce;
            AllocationId = allocationId;
            ConnectionData = connectionData;
            HostConnectionData = hostConnectionData;
            HMACKey = key;

            IsSecure = isSecure ? (byte)1 : (byte)0;

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref connectionData, ref key);
            }

            HostString = endpoint.ToFixedString();
        }

        public void IncrementNonce()
        {
            Nonce++;

            fixed(byte* hmacPtr = HMAC)
            {
                ComputeBindHMAC(hmacPtr, Nonce, ref ConnectionData, ref HMACKey);
            }
        }

        private static void ComputeBindHMAC(byte* result, ushort nonce, ref RelayConnectionData connectionData, ref RelayHMACKey key)
        {
            const int keyArrayLength = 64;
            var keyArray = stackalloc byte[keyArrayLength];

            fixed(byte* keyValue = &key.Value[0])
            {
                UnsafeUtility.MemCpy(keyArray, keyValue, keyArrayLength);

                const int messageLength = 263;

                var messageBytes = stackalloc byte[messageLength];

                messageBytes[0] = 0xDA;
                messageBytes[1] = 0x72;
                // ... zeros
                messageBytes[5] = (byte)nonce;
                messageBytes[6] = (byte)(nonce >> 8);
                messageBytes[7] = 255;

                fixed(byte* connValue = &connectionData.Value[0])
                {
                    UnsafeUtility.MemCpy(messageBytes + 8, connValue, 255);
                }

                HMACSHA256.ComputeHash(keyValue, keyArrayLength, messageBytes, messageLength, result);
            }
        }

        private static NetworkEndpoint HostToEndpoint(string host, ushort port)
        {
            NetworkEndpoint endpoint;

            if (NetworkEndpoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv4))
                return endpoint;

            if (NetworkEndpoint.TryParse(host, port, out endpoint, NetworkFamily.Ipv6))
                return endpoint;

            // If IPv4 and IPv6 parsing didn't work, we're dealing with a hostname. In this case,
            // perform a DNS resolution to figure out what its underlying IP address is. For WebGL,
            // use a hardcoded IP address since most browsers don't support making DNS resolutions
            // directly from JavaScript. This is safe to do since on WebGL the network interface
            // will never make use of actual endpoints (other than to put in the connection list).
#if UNITY_WEBGL && !UNITY_EDITOR
            return NetworkEndpoint.AnyIpv4.WithPort(port);
#else
            var addresses = Dns.GetHostEntry(host).AddressList;
            if (addresses.Length > 0)
            {
                var address = addresses[0].ToString();
                var family = addresses[0].AddressFamily;
                return NetworkEndpoint.Parse(address, port, (NetworkFamily)family);
            }

            DebugLog.ErrorRelayMapHostFailure(host);
            return default;
#endif
        }
    }
}
