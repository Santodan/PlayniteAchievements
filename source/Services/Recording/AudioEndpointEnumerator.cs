using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PlayniteAchievements.Services.Recording
{
    internal enum AudioDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2,
    }

    internal enum AudioEndpointRole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    /// <summary>
    /// One endpoint's identity, read once into plain strings. Nothing downstream holds a COM
    /// reference or needs an audio stack, so the classification built on this unit-tests freely.
    /// </summary>
    internal sealed class EndpointIdentity
    {
        public EndpointIdentity(
            string id,
            string friendlyName,
            string deviceFriendlyName,
            string instanceId,
            IReadOnlyList<string> propertyStrings)
        {
            Id = id;
            FriendlyName = friendlyName;
            DeviceFriendlyName = deviceFriendlyName;
            InstanceId = instanceId;
            PropertyStrings = propertyStrings ?? new string[0];
        }

        public string Id { get; }

        public string FriendlyName { get; }

        public string DeviceFriendlyName { get; }

        public string InstanceId { get; }

        /// <summary>Every string the endpoint's property store holds, vector elements flattened.</summary>
        public IReadOnlyList<string> PropertyStrings { get; }

        public string Describe()
        {
            return FriendlyName ?? DeviceFriendlyName ?? "unnamed";
        }

        public bool IsSame(EndpointIdentity other)
        {
            return other != null && !string.IsNullOrEmpty(Id) &&
                   string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The plugin's only route to the Windows audio endpoints: default endpoint ids, the active
    /// endpoint inventory, endpoint identity, and interface activation.
    /// <para>
    /// Every entry point creates <c>MMDeviceEnumerator</c> from its CLSID and casts only to a
    /// <c>[ComImport]</c> <em>interface</em>. NAudio declares its own managed coclass for the same
    /// CLSID, the CLR's CLSID-to-type map is process-wide and first-writer-wins, and Playnite loads
    /// every extension into one process -- so a second extension shipping NAudio makes
    /// <c>new MMDeviceEnumerator()</c> throw "Unable to cast object of type
    /// MMDeviceEnumeratorComObject to type MMDeviceEnumeratorComObject", and it takes all clip
    /// audio with it. Interface casts go through <c>QueryInterface</c> and are immune. No code in
    /// this plugin may reach the audio endpoints any other way.
    /// </para>
    /// </summary>
    internal static class AudioEndpointEnumerator
    {
        private const int DeviceStateActive = 0x1;
        private const int StgmRead = 0;
        private const int CLSCTX_ALL = 23;
        private const int E_FAIL = unchecked((int)0x80004005);

        // A vector this long is a corrupt read rather than a device property; bail before walking it.
        private const int MaxVectorElements = 4096;

        private const ushort VT_BSTR = 8;
        private const ushort VT_LPSTR = 30;
        private const ushort VT_LPWSTR = 31;
        private const ushort VT_VECTOR = 0x1000;

        private static readonly Guid MMDeviceEnumeratorClsid =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        private static readonly PropertyKey DeviceFriendlyNameKey =
            new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

        private static readonly PropertyKey InterfaceFriendlyNameKey =
            new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 6);

        // The endpoint's device instance path. Deliberately not PKEY_Device_InstanceId
        // ({78c34fc8-...}, 256): that key belongs to the device node and is absent from an
        // endpoint's own property store, so reading it there yields nothing on any machine.
        private static readonly PropertyKey InstancePathKey =
            new PropertyKey(new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), 2);

        // PROPVARIANT: an 8-byte header (VARTYPE plus three pad WORDs) then a pointer-aligned union
        // whose largest member used here (CALPWSTR) is a count followed by a pointer.
        private static readonly int PropVariantSize = 8 + (2 * IntPtr.Size);

        /// <summary>The default endpoint's id for this flow and role, or null when there is none.</summary>
        public static string TryGetDefaultEndpointId(AudioDataFlow flow, AudioEndpointRole role)
        {
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out var device) != 0 ||
                    device == null)
                {
                    return null;
                }

                try
                {
                    return device.GetId(out var id) == 0 ? id : null;
                }
                finally
                {
                    Release(device);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Release(enumerator);
            }
        }

        /// <summary>The default endpoint's identity for this flow and role, or null when there is none.</summary>
        public static EndpointIdentity TryGetDefaultEndpoint(AudioDataFlow flow, AudioEndpointRole role)
        {
            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out var device) != 0 ||
                    device == null)
                {
                    return null;
                }

                try
                {
                    return ReadIdentity(device);
                }
                finally
                {
                    Release(device);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                Release(enumerator);
            }
        }

        /// <summary>Every active endpoint on this flow. Throws when the enumeration itself fails.</summary>
        public static List<EndpointIdentity> EnumerateActive(AudioDataFlow flow)
        {
            var found = new List<EndpointIdentity>();
            IMMDeviceEnumerator enumerator = null;
            IMMDeviceCollection collection = null;
            try
            {
                enumerator = CreateEnumerator();
                var hr = enumerator.EnumAudioEndpoints((int)flow, DeviceStateActive, out collection);
                if (hr != 0 || collection == null)
                {
                    Marshal.ThrowExceptionForHR(hr != 0 ? hr : E_FAIL);
                }

                if (collection.GetCount(out var count) != 0)
                {
                    return found;
                }

                for (var index = 0; index < count; index++)
                {
                    if (collection.Item(index, out var device) != 0 || device == null)
                    {
                        continue;
                    }

                    try
                    {
                        found.Add(ReadIdentity(device));
                    }
                    finally
                    {
                        Release(device);
                    }
                }

                return found;
            }
            finally
            {
                Release(collection);
                Release(enumerator);
            }
        }

        /// <summary>
        /// Activates an interface on one endpoint by id. <c>IMMDevice::Activate</c> rather than
        /// <c>ActivateAudioInterfaceAsync</c>: that one takes a device interface path and is gated
        /// on a Windows build, while this needs neither.
        /// </summary>
        public static object ActivateEndpointInterface(string endpointId, Guid interfaceId)
        {
            if (string.IsNullOrEmpty(endpointId))
            {
                throw new ArgumentNullException(nameof(endpointId));
            }

            IMMDeviceEnumerator enumerator = null;
            try
            {
                enumerator = CreateEnumerator();
                var hr = enumerator.GetDevice(endpointId, out var device);
                if (hr != 0 || device == null)
                {
                    Marshal.ThrowExceptionForHR(hr != 0 ? hr : E_FAIL);
                }

                try
                {
                    var iid = interfaceId;
                    hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var instance);
                    if (hr != 0 || instance == null)
                    {
                        Marshal.ThrowExceptionForHR(hr != 0 ? hr : E_FAIL);
                    }

                    return instance;
                }
                finally
                {
                    Release(device);
                }
            }
            finally
            {
                Release(enumerator);
            }
        }

        private static IMMDeviceEnumerator CreateEnumerator()
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(
                Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid));
        }

        private static EndpointIdentity ReadIdentity(IMMDevice device)
        {
            string id = null;
            try
            {
                if (device.GetId(out var raw) == 0)
                {
                    id = raw;
                }
            }
            catch
            {
            }

            string friendlyName = null;
            string deviceFriendlyName = null;
            string instanceId = null;
            var strings = new List<string>();

            IPropertyStore store = null;
            try
            {
                if (device.OpenPropertyStore(StgmRead, out store) == 0 && store != null)
                {
                    friendlyName = ReadSingleString(store, DeviceFriendlyNameKey);
                    deviceFriendlyName = ReadSingleString(store, InterfaceFriendlyNameKey);
                    instanceId = ReadSingleString(store, InstancePathKey);
                    CollectAllStrings(store, strings);
                }
            }
            catch
            {
            }
            finally
            {
                Release(store);
            }

            return new EndpointIdentity(id, friendlyName, deviceFriendlyName, instanceId, strings);
        }

        private static string ReadSingleString(IPropertyStore store, PropertyKey key)
        {
            var values = new List<string>();
            ReadValue(store, key, values);
            return values.Count > 0 ? values[0] : null;
        }

        /// <summary>
        /// Every string in the store. Which property carries a device's vendor/product pair varies
        /// by driver, so the classifier is given all of them rather than a guessed shortlist.
        /// </summary>
        private static void CollectAllStrings(IPropertyStore store, List<string> into)
        {
            if (store.GetCount(out var count) != 0)
            {
                return;
            }

            for (var index = 0; index < count; index++)
            {
                if (store.GetAt(index, out var key) != 0)
                {
                    continue;
                }

                ReadValue(store, key, into);
            }
        }

        private static void ReadValue(IPropertyStore store, PropertyKey key, List<string> into)
        {
            var buffer = Marshal.AllocCoTaskMem(PropVariantSize);
            try
            {
                // Zeroed first: GetValue leaves the buffer untouched when it fails, and
                // PropVariantClear over uninitialised memory would free a stray pointer.
                for (var offset = 0; offset < PropVariantSize; offset += 4)
                {
                    Marshal.WriteInt32(buffer, offset, 0);
                }

                if (store.GetValue(ref key, buffer) != 0)
                {
                    return;
                }

                try
                {
                    ExtractStrings(buffer, into);
                }
                finally
                {
                    try { PropVariantClear(buffer); } catch { }
                }
            }
            catch
            {
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        private static void ExtractStrings(IntPtr propVariant, List<string> into)
        {
            var vt = unchecked((ushort)Marshal.ReadInt16(propVariant));
            var payload = propVariant + 8;

            if (vt == VT_LPWSTR || vt == VT_BSTR)
            {
                Add(into, ReadUni(Marshal.ReadIntPtr(payload)));
                return;
            }

            if (vt == VT_LPSTR)
            {
                var ansi = Marshal.ReadIntPtr(payload);
                Add(into, ansi == IntPtr.Zero ? null : Marshal.PtrToStringAnsi(ansi));
                return;
            }

            if (vt != (VT_VECTOR | VT_LPWSTR) && vt != (VT_VECTOR | VT_BSTR))
            {
                return;
            }

            // CALPWSTR: a count, then -- pointer-aligned, so one pointer along on either
            // architecture -- the array of string pointers.
            var elementCount = Marshal.ReadInt32(payload);
            var elements = Marshal.ReadIntPtr(payload + IntPtr.Size);
            if (elementCount <= 0 || elementCount > MaxVectorElements || elements == IntPtr.Zero)
            {
                return;
            }

            for (var index = 0; index < elementCount; index++)
            {
                Add(into, ReadUni(Marshal.ReadIntPtr(elements + (index * IntPtr.Size))));
            }
        }

        private static string ReadUni(IntPtr pointer)
        {
            return pointer == IntPtr.Zero ? null : Marshal.PtrToStringUni(pointer);
        }

        private static void Add(List<string> into, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                into.Add(value);
            }
        }

        private static void Release(object comObject)
        {
            if (comObject == null)
            {
                return;
            }

            try { Marshal.ReleaseComObject(comObject); } catch { }
        }

        [DllImport("ole32.dll", ExactSpelling = true, PreserveSig = true)]
        private static extern int PropVariantClear(IntPtr propVariant);

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public PropertyKey(Guid formatId, int propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }

            public Guid FormatId;
            public int PropertyId;
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

            [PreserveSig]
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

            [PreserveSig]
            int RegisterEndpointNotificationCallback(IntPtr client);

            [PreserveSig]
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig]
            int GetCount(out int count);

            [PreserveSig]
            int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                ref Guid interfaceId, int classContext, IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);

            [PreserveSig]
            int OpenPropertyStore(int access, out IPropertyStore properties);

            [PreserveSig]
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            [PreserveSig]
            int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out int count);

            [PreserveSig]
            int GetAt(int index, out PropertyKey key);

            [PreserveSig]
            int GetValue(ref PropertyKey key, IntPtr value);

            [PreserveSig]
            int SetValue(ref PropertyKey key, IntPtr value);

            [PreserveSig]
            int Commit();
        }
    }
}
