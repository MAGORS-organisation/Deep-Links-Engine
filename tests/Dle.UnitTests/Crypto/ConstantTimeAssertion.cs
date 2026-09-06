using System.Reflection;
using System.Security.Cryptography;

namespace Dle.UnitTests.Crypto;

/// <summary>
/// Proves that a comparison of secret material goes through
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>.
/// </summary>
/// <remarks>
/// <para>
/// The obvious test — measure two comparisons and assert the times are close — is worthless: it is
/// flaky on a shared runner, it is defeated by the JIT and by branch prediction, and a green result
/// proves nothing. So the assertion is structural instead: read the compiled method body and look
/// for the call. That answers the question the review actually asks — "does this code path use the
/// fixed-time comparison?" — deterministically and in microseconds.
/// </para>
/// <para>
/// The scan walks the IL looking for <c>call</c> (0x28) and <c>callvirt</c> (0x6F) opcodes and
/// resolves the four-byte metadata token that follows each one. It is deliberately conservative: a
/// token that cannot be resolved is skipped rather than failing the scan, because the only question
/// being answered is whether a particular call is present.
/// </para>
/// </remarks>
internal static class ConstantTimeAssertion
{
    private const byte Call = 0x28;
    private const byte CallVirt = 0x6F;

    /// <summary>
    /// Whether the named method of <paramref name="declaringType"/> calls
    /// <c>CryptographicOperations.FixedTimeEquals</c>.
    /// </summary>
    /// <param name="declaringType">Type declaring the method.</param>
    /// <param name="methodName">Name of the method, public or not.</param>
    /// <returns><see langword="true"/> when the call is present.</returns>
    internal static bool CallsFixedTimeEquals(Type declaringType, string methodName)
    {
        ArgumentNullException.ThrowIfNull(declaringType);

        MethodInfo method = declaringType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{declaringType.Name}.{methodName} does not exist.");

        return CalledMethods(method).Any(called =>
            called.DeclaringType == typeof(CryptographicOperations) &&
            string.Equals(called.Name, nameof(CryptographicOperations.FixedTimeEquals), StringComparison.Ordinal));
    }

    private static IEnumerable<MethodBase> CalledMethods(MethodInfo method)
    {
        MethodBody body = method.GetMethodBody()
            ?? throw new InvalidOperationException($"{method.Name} has no body to inspect.");

        byte[] il = body.GetILAsByteArray()
            ?? throw new InvalidOperationException($"{method.Name} exposes no IL.");

        Module module = method.Module;
        Type[] typeArguments = method.DeclaringType?.GetGenericArguments() ?? [];
        Type[] methodArguments = method.GetGenericArguments();

        for (int offset = 0; offset + 4 < il.Length; offset++)
        {
            if (il[offset] is not (Call or CallVirt))
            {
                continue;
            }

            int token = BitConverter.ToInt32(il, offset + 1);
            MethodBase? called;

            try
            {
                called = module.ResolveMethod(token, typeArguments, methodArguments);
            }
#pragma warning disable CA1031 // A token that is not a method reference is simply not the call we are looking for.
            catch (Exception)
#pragma warning restore CA1031
            {
                continue;
            }

            if (called is not null)
            {
                yield return called;
            }
        }
    }
}
