namespace QuotaDeck.Readers.Tests.Claude;

using System.Reflection;
using System.Runtime.InteropServices;
using QuotaDeck.Readers.Shared;

/// <summary>
/// The shape of the Security.framework declarations, checked by reflection.
///
/// The C `Boolean` is one byte, and a managed `bool` is marshalled as a four-byte BOOL unless the
/// return is marked `UnmanagedType.U1` — the upper register bytes can then flip a true into a false.
/// No keychain can be pointed at from a unit test, so this is the half that can be: the declarations
/// themselves, audited as a set so a future call added without the marshalling fails here.
/// </summary>
public class MacKeychainDeclarationTests
{
    private static Type Native => typeof(MacKeychain)
        .GetNestedType("Native", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("MacKeychain.Native is gone");

    private static List<MethodInfo> Declarations => Native
        .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
        .ToList();

    [Fact]
    public void EveryBooleanInTheNativeDeclarationsIsMarshalledAsOneByte()
    {
        var declarations = Declarations;
        Assert.NotEmpty(declarations);

        foreach (var method in declarations)
        {
            if (method.ReturnType == typeof(bool))
            {
                Assert.Equal(
                    UnmanagedType.U1,
                    method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value);
            }

            foreach (var parameter in method.GetParameters())
            {
                if (parameter.ParameterType == typeof(bool) || parameter.ParameterType == typeof(bool).MakeByRefType())
                {
                    Assert.Equal(UnmanagedType.U1, parameter.GetCustomAttribute<MarshalAsAttribute>()?.Value);
                }
            }
        }
    }

    /// <summary>The one that was wrong: `CFStringGetCString` returns C's one-byte `Boolean`.</summary>
    [Fact]
    public void CFStringGetCStringReturnsAOneByteBoolean()
    {
        var method = Declarations.Single(candidate => candidate.Name == "CFStringGetCString");

        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Equal(UnmanagedType.U1, method.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>()?.Value);
    }

    /// <summary>
    /// `SecKeychainGetUserInteractionAllowed` answers through a Darwin `Boolean`, which is an
    /// unsigned byte — a managed `bool` out-parameter would read four bytes off the stack.
    /// </summary>
    [Fact]
    public void TheInteractionSwitchIsDeclaredWithDarwinBooleanWidths()
    {
        var get = Declarations.Single(candidate => candidate.Name == "SecKeychainGetUserInteractionAllowed");
        Assert.Equal(typeof(byte), Element(get.GetParameters().Single().ParameterType));

        var set = Declarations.Single(candidate => candidate.Name == "SecKeychainSetUserInteractionAllowed");
        Assert.Equal(typeof(byte), Element(set.GetParameters().Single().ParameterType));
    }

    /// <summary>The element type of a by-ref parameter — an `out byte` reports as `byte&`.</summary>
    private static Type Element(Type type) => type.IsByRef ? type.GetElementType()! : type;

    /// <summary>
    /// The dictionary is built with the framework's own type callbacks, which `dlsym` hands over as
    /// the address of an exported struct. A null callback would leave the query dictionary
    /// non-retaining, which is not the dictionary Codenotch builds.
    /// </summary>
    [Fact]
    public void TheDictionaryTypeCallbacksAreResolved()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        foreach (var name in (string[])["KeyCallbacks", "ValueCallbacks"])
        {
            var field = Native.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(field);
            Assert.NotEqual(IntPtr.Zero, (IntPtr)field!.GetValue(null)!);
        }
    }
}
