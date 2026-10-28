using System;
using System.Text;
using HappyEngine.Base;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// F12 switches to the next skill plan, which carries its own skill bar (up to five hotbars). The key press only tells the server
    /// (game message <see cref="MsgId"/>, empty body); the server picks the next unlocked plan and answers with the client's own
    /// SWITCH_PLAN reply and skill data sync, so the bar redraws through the normal path. Ignored while a text box has the keyboard.
    /// Kill switch: environment variable RO3EMU_NO_HOTBAR_KEY=1.
    /// </summary>
    public static class HotbarKey
    {
        /// <summary>Must equal the server's SkillService.NextPlanMsg.</summary>
        public const uint MsgId = 77018;

        private static GameObject _object;

        internal static bool Disabled => Environment.GetEnvironmentVariable("RO3EMU_NO_HOTBAR_KEY") == "1";

        /// <summary>From LuaPatches.Tick (once per LuaEnv); creates the key listener once.</summary>
        internal static void Install()
        {
            try
            {
                if (Disabled || _object != null) return; // Unity null check: a destroyed object is recreated
                _object = new GameObject("Ro3Emu_HotbarKey");
                _object.hideFlags = HideFlags.HideInHierarchy;
                UnityEngine.Object.DontDestroyOnLoad(_object);
                _object.AddComponent<HotbarKeyBehaviour>();
                Debug.Log("[Ro3Emu.Hotbar] F12 switches to the next skill plan");
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Hotbar] install failed: " + e);
            }
        }

        internal static void Press()
        {
            try
            {
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(MsgId, new byte[0]);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Hotbar] send failed: " + e);
            }
        }

        /// <summary>True while a Unity UI text input (uGUI or TextMeshPro, e.g. the chat box) has keyboard focus.</summary>
        internal static bool TextInputFocused()
        {
            try
            {
                var system = EventSystem.current;
                var selected = system != null ? system.currentSelectedGameObject : null;
                if (selected == null) return false;
                var tmp = selected.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var legacy = selected.GetComponent<InputField>();
                return legacy != null && legacy.isFocused;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal sealed class HotbarKeyBehaviour : MonoBehaviour
    {
        private float _last;

        private void OnGUI()
        {
            var ev = Event.current;
            if (ev == null || ev.type != EventType.KeyDown || ev.keyCode != KeyCode.F12) return;
            if (HotbarKey.TextInputFocused()) return;
            float now = Time.unscaledTime;
            if (now - _last < 0.4f) return; // key auto-repeat
            _last = now;
            HotbarKey.Press();
            ev.Use();
        }
    }
}
