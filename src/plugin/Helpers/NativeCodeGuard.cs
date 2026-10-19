using Il2CppInterop.Common;
using System;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MegabonkTogether.Helpers
{
    /// <summary>
    /// IL2CPP builds fold byte-identical functions into one: an empty or forwarding method shares its native code with many
    /// others (e.g. UpgradeButton.OnClick is a lone "ret" also called by SpawnPositions' static constructor). Patching it
    /// hooks all of them and crashes the game on start.
    /// </summary>
    internal static class NativeCodeGuard
    {
        public static bool IsSafeToPatch(MethodBase method)
        {
            try
            {
                if (HarmonyLib.Harmony.GetPatchInfo(method) != null)
                {
                    return true; //Already hooked by another patch: its code now starts with a jump to the hook
                }

                var methodInfoField = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                var methodInfo = methodInfoField != null ? (IntPtr)methodInfoField.GetValue(null) : IntPtr.Zero;
                if (methodInfo == IntPtr.Zero)
                {
                    return false;
                }

                var code = Marshal.ReadIntPtr(methodInfo); // Il2CppMethodInfo.methodPointer
                if (code == IntPtr.Zero)
                {
                    return false;
                }

                var first = Marshal.ReadByte(code);
                var second = Marshal.ReadByte(code, 1);
                var isTiny = first == 0xC3 || first == 0xC2 // ret: empty method
                    || first == 0xE9 || first == 0xEB // jmp: tail call only
                    || (first == 0xFF && second == 0x25) || (first == 0x48 && second == 0xFF); // jmp [rip+x]

                if (isTiny)
                {
                    Plugin.Log.LogWarning($"Not patching {method.DeclaringType?.Name}.{method.Name}: its native code is shared with other methods");
                }

                return !isTiny;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Not patching {method.DeclaringType?.Name}.{method.Name}: can't inspect its native code ({ex.Message})");
                return false;
            }
        }
    }
}
