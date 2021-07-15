using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MelonLoader;
using TrueShaderAntiCrash;
using UIExpansionKit.API;
using UnityEngine.SceneManagement;
using VRC.Core;

[assembly:MelonInfo(typeof(TrueShaderAntiCrashMod), "True Shader Anticrash", "1.0.2", "knah", "https://github.com/knah/VRCMods")]
[assembly:MelonGame("VRChat", "VRChat")]

namespace TrueShaderAntiCrash
{
    internal partial class TrueShaderAntiCrashMod : MelonMod
    {
        public override void OnApplicationStart()
        {
            var pluginsPath = MelonUtils.GetGameDataDirectory() + "/Plugins";
            var dllName = ShaderFilterApi.DLLName + ".dll";

            try
            {
                using var resourceStream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(typeof(TrueShaderAntiCrashMod), dllName);
                using var fileStream = File.Open(pluginsPath + "/" + dllName, FileMode.Create, FileAccess.Write);

                resourceStream.CopyTo(fileStream);
            }
            catch (IOException ex)
            {
                MelonLogger.Warning("Failed to write native unity plugin; will attempt loading it anyway. This is normal if you're running multiple instances of VRChat");
                MelonDebug.Msg(ex.ToString());
            }

            var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (!module.FileName.Contains("UnityPlayer")) continue;
                
                var loadLibraryAddress = module.BaseAddress + 0x819130;
                var dg = Marshal.GetDelegateForFunctionPointer<FindAndLoadUnityPlugin>(loadLibraryAddress);

                var strPtr = Marshal.StringToHGlobalAnsi(ShaderFilterApi.DLLName);

                dg(strPtr, out var loaded);

                if (loaded == IntPtr.Zero)
                {
                    MelonLogger.Error("Module load failed");
                    return;
                }

                ShaderFilterApi.Init(loaded);

                Marshal.FreeHGlobal(strPtr);

                break;
            }

            var category = MelonPreferences.CreateCategory("True Shader Anticrash");
            
            var loopsEnabled = category.CreateEntry("LimitLoops", true, "Limit loops");
            var geometryEnabled = category.CreateEntry("LimitGeometry", true, "Limit geometry shaders");
            var tessEnabled = category.CreateEntry("LimitTesselation", true, "Limit tesselation");

            MelonPreferences_Entry<bool> enabledInPublicsOnly = null;

            IEnumerator WaitForRoomManagerAndUpdate()
            {
                while (RoomManager.field_Internal_Static_ApiWorldInstance_0 == null)
                    yield return null;
                
                UpdateLimiters();
            }

            void UpdateLimiters()
            {
                if (enabledInPublicsOnly.Value)
                {
                    var room = RoomManager.field_Internal_Static_ApiWorldInstance_0;
                    if (room == null)
                    {
                        MelonCoroutines.Start(WaitForRoomManagerAndUpdate());
                        return;
                    }

                    if (room.type != InstanceAccessType.Public)
                    {
                        ShaderFilterApi.SetFilteringState(false, false, false);
                        return;
                    }
                }
                
                ShaderFilterApi.SetFilteringState(loopsEnabled.Value, geometryEnabled.Value, tessEnabled.Value);
            }

            loopsEnabled.OnValueChanged += (_, value) => UpdateLimiters();
            geometryEnabled.OnValueChanged += (_, value) => UpdateLimiters();
            tessEnabled.OnValueChanged += (_, value) => UpdateLimiters();

            var maxLoopIterations = category.CreateEntry("MaxLoopIterations", 128, "Max loop iterations");
            maxLoopIterations.OnValueChanged += (_, value) => ShaderFilterApi.SetLoopLimit(value);

            var maxGeometry = category.CreateEntry("MaxGeometryOutputs", 60, "Max geometry shader outputs");
            maxGeometry.OnValueChanged += (_, value) => ShaderFilterApi.SetLoopLimit(value);
            
            var maxTess = category.CreateEntry("MaxTesselation", 5f, "Max tesselation power");
            maxTess.OnValueChanged += (_, value) => ShaderFilterApi.SetMaxTesselationPower(value);

            var enabledForWorlds = category.CreateEntry("DisableDuringWorldLoad", true, "Try to avoid affecting world shaders");
            enabledInPublicsOnly = category.CreateEntry("EnabledInPublicsOnly", false, "Only enabled in public instances");
            
            SceneManager.add_sceneLoaded(new Action<Scene, LoadSceneMode>((sc, _) =>
            {
                if (sc.buildIndex == -1)
                    UpdateLimiters();
            }));
            
            SceneManager.add_sceneUnloaded(new Action<Scene>(_ =>
            {
                if (enabledForWorlds.Value)
                    ShaderFilterApi.SetFilteringState(false, false, false);
            }));
            
            UpdateLimiters();
            ShaderFilterApi.SetMaxTesselationPower(maxTess.Value);
            ShaderFilterApi.SetLoopLimit(maxLoopIterations.Value);
            ShaderFilterApi.SetGeometryLimit(maxGeometry.Value);

            if (MelonHandler.Mods.Any(it =>
                it.Assembly.GetName().Name == "UIExpansionKit" &&
                it.Assembly.GetName().Version >= new Version(0, 2, 4))) 
                AddNewUixProperties(category.Identifier);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AddNewUixProperties(string categoryName)
        {
            ExpansionKitApi.GetSettingsCategory(categoryName).AddLabel("World rejoin is required to apply settings");
        }

        [UnmanagedFunctionPointer(CallingConvention.FastCall)]
        private delegate void FindAndLoadUnityPlugin(IntPtr name, out IntPtr loadedModule);
    }
}