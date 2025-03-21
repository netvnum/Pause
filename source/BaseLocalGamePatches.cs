using EFT;
using SPT.Reflection.Patching;
using System.Reflection;

namespace Pause
{
    public class BaseLocalGameUpdatePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return typeof(BaseLocalGame<EftGamePlayerOwner>).GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
        }

        [PatchPrefix]
        internal static bool Prefix()
        {
            return !PauseController.IsPaused;
        }
    }
}
