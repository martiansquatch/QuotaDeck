namespace QuotaDeck.Readers.Shared;

using System.Runtime.InteropServices;

// Port of Codenotch Sources/Providers/KeychainItem.swift (enum KeychainItem: newest(service:account:),
// winner(among:), modifiedAt(service:account:), newest(services:account:)) and the reading half of
// Sources/Providers/KeychainPrompt.swift (enum KeychainSecret.read(query:interactive:rescue:):
// SecKeychainGetUserInteractionAllowed / SecKeychainSetUserInteractionAllowed around a
// SecItemCopyMatching, under one process-wide lock, with kSecUseAuthenticationUI = fail).
//
// The seam is here rather than in one provider because Cursor, Muse and Antigravity read the same
// login keychain the same way: enumerate attributes without prompting, then read one item's data by
// persistent reference with interaction off. The refusal rescue (`/usr/bin/security`) is *not* here:
// it runs a process, and each provider decides for itself what a refusal means.

/// <summary>One matching keychain item, without its secret.</summary>
/// <param name="ModifiedAt">
/// When the owning app last wrote it, where macOS cared to say. Never read from the data.
/// </param>
/// <param name="PersistentRef">An opaque handle that can fetch the data later without searching again.</param>
/// <param name="Service">The service name it was filed under, needed to reach it by name on a rescue.</param>
public sealed record KeychainItem(DateTimeOffset? ModifiedAt, byte[] PersistentRef, string Service);

/// <summary>
/// The status and bytes of one data read. <see cref="KeychainStatus.Success"/> with a null
/// <see cref="Data"/> means the item answered with nothing, which macOS can do.
/// </summary>
public sealed record KeychainReadResult(int Status, byte[]? Data);

/// <summary>
/// The keychain, as providers use it. Implementations must never raise a prompt: enumeration reads
/// attributes only, and a data read sets interaction off (<c>kSecUseAuthenticationUIFail</c> and
/// <c>SecKeychainSetUserInteractionAllowed(false)</c>) so macOS answers with a refusal status
/// instead of a dialogue.
/// </summary>
public interface IKeychain
{
    /// <summary>Every item under one service, attributes and persistent refs only — never prompts.</summary>
    IReadOnlyList<KeychainItem> Items(string service, string? account = null);

    /// <summary>One item's data, by the reference <see cref="Items"/> handed back. May refuse.</summary>
    KeychainReadResult ReadItem(byte[] persistentRef);
}

/// <summary>
/// The selection rules shared by every implementation: the newest item across several services, ties
/// going either way, and an item with no modification date kept rather than discarded.
///
/// Mirrors <c>KeychainItem.newest(services:account:)</c> and <c>KeychainItem.winner(among:)</c>.
/// </summary>
public static class KeychainQuery
{
    /// <summary>The most recently modified item across these services, or nil when there is none.</summary>
    public static KeychainItem? Newest(IKeychain keychain, IReadOnlyList<string> services, string? account = null)
    {
        ArgumentNullException.ThrowIfNull(keychain);
        ArgumentNullException.ThrowIfNull(services);

        KeychainItem? winner = null;
        foreach (var service in services)
        {
            foreach (var item in keychain.Items(service, account))
            {
                if (winner is null
                    || (item.ModifiedAt ?? DateTimeOffset.MinValue) > (winner.ModifiedAt ?? DateTimeOffset.MinValue))
                {
                    winner = item;
                }
            }
        }

        return winner;
    }

    /// <summary>When the owning app last wrote the newest item across these services, or nil.</summary>
    public static DateTimeOffset? ModifiedAt(IKeychain keychain, IReadOnlyList<string> services, string? account = null) =>
        Newest(keychain, services, account)?.ModifiedAt;
}

/// <summary>
/// The OSStatus values this app tells apart.
///
/// Port of the status handling in <c>ClaudeCredentials.swift</c> (<c>wasRefused</c>,
/// <c>wasTransient</c>, <c>explain</c>). Security.framework does not export a name for every one of
/// them (there is no <c>errSecInDarkWake</c>), so the raw values are what there is to check.
/// </summary>
public static class KeychainStatus
{
    public const int Success = 0;

    /// <summary>errSecItemNotFound.</summary>
    public const int ItemNotFound = -25300;

    /// <summary>The item is there and this app is not on its access list (errSecAuthFailed).</summary>
    public const int AuthFailed = -25293;

    /// <summary>Someone dismissed or denied the dialogue (errSecUserCanceled).</summary>
    public const int UserCanceled = -128;

    /// <summary>The same refusal arriving without a prompt (errSecInteractionNotAllowed).</summary>
    public const int InteractionNotAllowed = -25308;

    /// <summary>In dark wake, no UI possible — a Mac that just woke, not a verdict on the item.</summary>
    public const int InDarkWake = -25320;

    /// <summary>errAuthorizationInternal: a prompt was needed and could not be shown.</summary>
    public const int AuthorizationInternal = -60008;

    /// <summary>Whether macOS refused a credential that exists, rather than failing to find one.</summary>
    public static bool WasRefused(int status) =>
        status == AuthFailed || status == UserCanceled || status == InteractionNotAllowed;

    /// <summary>A read that failed for a reason with nothing to do with the account.</summary>
    public static bool WasTransient(int status) => status == InDarkWake || status == AuthorizationInternal;

    /// <summary>The status in words, for a log line that has to tell "not found" from "refused".</summary>
    public static string Explain(int status) => status switch
    {
        Success => "success",
        ItemNotFound => "no such item — the owning app has not signed in",
        InteractionNotAllowed => "access not permitted without interaction",
        UserCanceled => "the access prompt was dismissed or denied",
        AuthFailed => "authorisation failed",
        InDarkWake => "in dark wake, no UI possible",
        AuthorizationInternal => "a prompt was needed and could not be shown",
        _ => $"OSStatus {status}",
    };
}

/// <summary>
/// The real keychain, through Security.framework.
///
/// A thin wrapper over five calls: <c>SecItemCopyMatching</c> twice (once for attributes, once for
/// data), <c>SecKeychainGetUserInteractionAllowed</c>/<c>SetUserInteractionAllowed</c> around the
/// data read, and the CoreFoundation calls needed to build a query and pull strings, bytes and
/// dates out of the answer. The constants are resolved with <c>dlsym</c> because P/Invoke cannot
/// reach an exported data symbol; the interaction switch is saved and restored under one
/// process-wide lock, exactly as <c>KeychainSecret.interactionLock</c> does — two readers
/// interleaving their save and restore could otherwise leave interaction off for good.
///
/// On a machine that is not macOS every query reports "not found": a missing keychain is a state the
/// callers already handle, and it keeps the reader logic one shape on every platform.
/// </summary>
public sealed class MacKeychain : IKeychain
{
    /// <summary>One switch for the whole process; every reader that flips it takes this lock.</summary>
    private static readonly object InteractionGate = new();

    public IReadOnlyList<KeychainItem> Items(string service, string? account = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(service);

        if (!OperatingSystem.IsMacOS())
        {
            return [];
        }

        using var query = Native.Query.Empty();
        query.Add(Native.Class, Native.ClassGenericPassword);
        query.AddString(Native.AttrService, service);
        if (account is not null)
        {
            query.AddString(Native.AttrAccount, account);
        }

        query.Add(Native.ReturnAttributes, Native.BooleanTrue);
        query.Add(Native.ReturnPersistentRef, Native.BooleanTrue);
        query.Add(Native.MatchLimit, Native.MatchLimitAll);

        var status = Native.SecItemCopyMatching(query.Build(), out var result);
        if (status != KeychainStatus.Success)
        {
            return [];
        }

        try
        {
            return Native.Items(result);
        }
        finally
        {
            Native.CFRelease(result);
        }
    }

    public KeychainReadResult ReadItem(byte[] persistentRef)
    {
        ArgumentNullException.ThrowIfNull(persistentRef);

        if (!OperatingSystem.IsMacOS())
        {
            return new KeychainReadResult(KeychainStatus.ItemNotFound, null);
        }

        // The one call that can prompt. Interaction is switched off for it — the legacy switch for
        // items that honour it, kSecUseAuthenticationUI for items that ignore the switch — so a
        // refusal is a status, never a dialogue nobody is there to answer.
        lock (InteractionGate)
        {
            Native.SecKeychainGetUserInteractionAllowed(out var wasAllowed);
            Native.SecKeychainSetUserInteractionAllowed(0);
            try
            {
                using var query = Native.Query.Empty();
                query.Add(Native.Class, Native.ClassGenericPassword);
                query.AddData(Native.ValuePersistentRef, persistentRef);
                query.Add(Native.ReturnData, Native.BooleanTrue);
                query.Add(Native.MatchLimit, Native.MatchLimitOne);
                query.Add(Native.UseAuthenticationUI, Native.UseAuthenticationUIFail);

                var status = Native.SecItemCopyMatching(query.Build(), out var result);
                if (status != KeychainStatus.Success || result == IntPtr.Zero)
                {
                    return new KeychainReadResult(status, null);
                }

                try
                {
                    return new KeychainReadResult(status, Native.Bytes(result));
                }
                finally
                {
                    Native.CFRelease(result);
                }
            }
            finally
            {
                Native.SecKeychainSetUserInteractionAllowed(wasAllowed);
            }
        }
    }

    /// <summary>
    /// The Security.framework calls and the constants they need. Every member here is only touched
    /// on macOS, so the framework is only loaded there.
    /// </summary>
    private static class Native
    {
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        private const string Security =
            "/System/Library/Frameworks/Security.framework/Security";

        private const string LibSystem = "/usr/lib/libSystem.B.dylib";

        private const uint EncodingUtf8 = 0x08000100;

        /// <summary>CFDate is seconds since 2001-01-01, not 1970.</summary>
        private const double CoreFoundationEpoch = 978307200;

        private const int RtldNow = 2;

        private static readonly IntPtr CoreFoundationHandle = DlOpen(CoreFoundation, RtldNow);

        private static readonly IntPtr SecurityHandle = DlOpen(Security, RtldNow);

        public static readonly IntPtr Class = Constant("kSecClass");

        public static readonly IntPtr ClassGenericPassword = Constant("kSecClassGenericPassword");

        public static readonly IntPtr AttrService = Constant("kSecAttrService");

        public static readonly IntPtr AttrAccount = Constant("kSecAttrAccount");

        public static readonly IntPtr AttrModificationDate = Constant("kSecAttrModificationDate");

        public static readonly IntPtr ValuePersistentRef = Constant("kSecValuePersistentRef");

        public static readonly IntPtr ReturnAttributes = Constant("kSecReturnAttributes");

        public static readonly IntPtr ReturnPersistentRef = Constant("kSecReturnPersistentRef");

        public static readonly IntPtr ReturnData = Constant("kSecReturnData");

        public static readonly IntPtr MatchLimit = Constant("kSecMatchLimit");

        public static readonly IntPtr MatchLimitOne = Constant("kSecMatchLimitOne");

        public static readonly IntPtr MatchLimitAll = Constant("kSecMatchLimitAll");

        public static readonly IntPtr UseAuthenticationUI = Constant("kSecUseAuthenticationUI");

        public static readonly IntPtr UseAuthenticationUIFail = Constant("kSecUseAuthenticationUIFail");

        public static readonly IntPtr BooleanTrue = Constant("kCFBooleanTrue");

        /// <summary>
        /// A CFDictionary built for one call. Values are the framework's own constants or objects
        /// created here; the created ones are released with the dictionary.
        /// </summary>
        public sealed class Query : IDisposable
        {
            private readonly List<(IntPtr Key, IntPtr Value)> _pairs = [];

            private readonly List<IntPtr> _created = [];

            private Query()
            {
            }

            public IntPtr Dictionary { get; private set; }

            public static Query Empty() => new();

            public void Add(IntPtr key, IntPtr value) => this._pairs.Add((key, value));

            public void AddString(IntPtr key, string value)
            {
                var utf8 = Marshal.StringToCoTaskMemUTF8(value);
                IntPtr created;
                try
                {
                    created = CFStringCreateWithCString(IntPtr.Zero, utf8, EncodingUtf8);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(utf8);
                }

                this._created.Add(created);
                this.Add(key, created);
            }

            public void AddData(IntPtr key, byte[] value)
            {
                var created = CFDataCreate(IntPtr.Zero, value, value.Length);
                this._created.Add(created);
                this.Add(key, created);
            }

            public IntPtr Build()
            {
                if (this.Dictionary == IntPtr.Zero && this._pairs.Count > 0)
                {
                    // A dictionary whose callbacks are null: every value is either a framework
                    // constant or an object created here, so no retain/release callback is wanted.
                    var keys = new IntPtr[this._pairs.Count];
                    var values = new IntPtr[this._pairs.Count];
                    for (var index = 0; index < this._pairs.Count; index++)
                    {
                        keys[index] = this._pairs[index].Key;
                        values[index] = this._pairs[index].Value;
                    }

                    this.Dictionary = CFDictionaryCreate(
                        IntPtr.Zero, keys, values, this._pairs.Count, IntPtr.Zero, IntPtr.Zero);
                }

                return this.Dictionary;
            }

            public void Dispose()
            {
                if (this.Dictionary != IntPtr.Zero)
                {
                    CFRelease(this.Dictionary);
                    this.Dictionary = IntPtr.Zero;
                }

                foreach (var value in this._created)
                {
                    if (value != IntPtr.Zero)
                    {
                        CFRelease(value);
                    }
                }

                this._created.Clear();
                this._pairs.Clear();
            }
        }

        /// <summary>
        /// The dictionaries inside a <c>SecItemCopyMatching</c> answer. A single match still comes
        /// back as one dictionary rather than an array of one — <c>kSecMatchLimitAll</c> promises
        /// "every match", not "an array".
        /// </summary>
        public static List<KeychainItem> Items(IntPtr result)
        {
            var items = new List<KeychainItem>();
            if (result == IntPtr.Zero)
            {
                return items;
            }

            var array = IsArray(result);
            var count = array ? (int)CFArrayGetCount(result) : 1;
            for (var index = 0; index < count; index++)
            {
                var item = array ? CFArrayGetValueAtIndex(result, index) : result;
                if (item == IntPtr.Zero)
                {
                    continue;
                }

                var reference = DictionaryData(item, ValuePersistentRef);
                if (reference is null)
                {
                    continue;
                }

                items.Add(new KeychainItem(
                    DictionaryDate(item, AttrModificationDate),
                    reference,
                    DictionaryString(item, AttrService) ?? string.Empty));
            }

            return items;
        }

        public static byte[]? Bytes(IntPtr cfData)
        {
            if (cfData == IntPtr.Zero || CFGetTypeID(cfData) != CFDataGetTypeID())
            {
                return null;
            }

            var length = CFDataGetLength(cfData);
            if (length <= 0)
            {
                return null;
            }

            var pointer = CFDataGetBytePtr(cfData);
            if (pointer == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[(int)length];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }

        private static bool IsArray(IntPtr value) => CFGetTypeID(value) == CFArrayGetTypeID();

        private static byte[]? DictionaryData(IntPtr dictionary, IntPtr key) =>
            Bytes(CFDictionaryGetValue(dictionary, key));

        private static DateTimeOffset? DictionaryDate(IntPtr dictionary, IntPtr key)
        {
            var value = CFDictionaryGetValue(dictionary, key);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFDateGetTypeID())
            {
                return null;
            }

            // Sub-second precision kept: the credential cache answers "has this item moved?" by
            // comparing stamps, and Claude Code rotates its token inside the same second often
            // enough that a whole-second stamp would miss the change.
            var absolute = CoreFoundationEpoch + CFDateGetAbsoluteTime(value);
            var seconds = (long)Math.Floor(absolute);
            var ticks = (long)Math.Round((absolute - seconds) * TimeSpan.TicksPerSecond);
            return DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(ticks);
        }

        private static string? DictionaryString(IntPtr dictionary, IntPtr key)
        {
            var value = CFDictionaryGetValue(dictionary, key);
            if (value == IntPtr.Zero || CFGetTypeID(value) != CFStringGetTypeID())
            {
                return null;
            }

            var length = CFStringGetLength(value);
            var size = CFStringGetMaximumSizeForEncoding(length, EncodingUtf8) + 1;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!CFStringGetCString(value, buffer, size, EncodingUtf8))
                {
                    return null;
                }

                return Marshal.PtrToStringUTF8(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr Constant(string name)
        {
            // The Security constants live in the Security framework; kCFBooleanTrue lives in
            // CoreFoundation and is re-exported, so Security is asked first in both cases.
            var symbol = DlSym(SecurityHandle, name);
            if (symbol == IntPtr.Zero)
            {
                symbol = DlSym(CoreFoundationHandle, name);
            }

            // dlsym answers with the address of the symbol; the constant is what is stored there.
            return symbol == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(symbol);
        }

        [DllImport(LibSystem, EntryPoint = "dlopen", CharSet = CharSet.Ansi)]
        private static extern IntPtr DlOpen(string path, int mode);

        [DllImport(LibSystem, EntryPoint = "dlsym", CharSet = CharSet.Ansi)]
        private static extern IntPtr DlSym(IntPtr handle, string symbol);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryCreate(
            IntPtr allocator, IntPtr[] keys, IntPtr[] values, nint numValues,
            IntPtr keyCallbacks, IntPtr valueCallbacks);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(IntPtr value);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, IntPtr cString, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern nint CFStringGetLength(IntPtr theString);

        [DllImport(CoreFoundation)]
        private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern bool CFStringGetCString(IntPtr theString, IntPtr buffer, nint bufferSize, uint encoding);

        [DllImport(CoreFoundation)]
        private static extern nint CFArrayGetCount(IntPtr theArray);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFArrayGetValueAtIndex(IntPtr theArray, nint index);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDictionaryGetValue(IntPtr theDictionary, IntPtr key);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

        [DllImport(CoreFoundation)]
        private static extern nint CFDataGetLength(IntPtr theData);

        [DllImport(CoreFoundation)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr theData);

        [DllImport(CoreFoundation)]
        private static extern double CFDateGetAbsoluteTime(IntPtr theDate);

        [DllImport(CoreFoundation)]
        private static extern nint CFGetTypeID(IntPtr cf);

        [DllImport(CoreFoundation)]
        private static extern nint CFArrayGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern nint CFStringGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern nint CFDateGetTypeID();

        [DllImport(CoreFoundation)]
        private static extern nint CFDataGetTypeID();

        [DllImport(Security)]
        public static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);

        [DllImport(Security)]
        public static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);

        [DllImport(Security)]
        public static extern int SecKeychainSetUserInteractionAllowed(byte allowed);
    }
}
