using System;
using System.Collections.Generic;
using System.Reflection;

namespace ThorsKist
{
    internal static class JournalSocketTransport
    {
        private const string ServerSyncWrapper = "ServerSync.ConfigSync+SendConfigsAfterLogin+BufferingSocket";
        private static readonly Dictionary<Type, FieldInfo> originals = new Dictionary<Type, FieldInfo>();
        internal static ISocket Unwrap(ISocket socket)
        {
            // Multiple mods embed independent ServerSync copies and can nest
            // wrappers. Bounded traversal also refuses malformed/cyclic chains.
            for (int depth = 0; socket != null && depth < 16; depth++)
            {
                var type = socket.GetType();
                if (type.FullName != ServerSyncWrapper) return socket;
                if (!originals.TryGetValue(type, out var original))
                {
                    original = type.GetField("Original", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
                    if (original == null || original.FieldType != typeof(ISocket) || !original.IsInitOnly) return null;
                    originals.Add(type, original);
                }
                socket = original.GetValue(socket) as ISocket;
            }
            return null;
        }
    }
}
