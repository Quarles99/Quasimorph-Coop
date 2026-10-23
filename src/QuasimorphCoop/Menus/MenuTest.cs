using System.Collections;
using System.IO;
using System.Linq;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop.Menus
{
    // Automated check of the co-op menus (enabled by menutest=1 in lockstep_test.cfg): opens the
    // submenu on the main menu and in the pause menu of a loaded mission, clicks the partner button,
    // and logs captions, party size and screenshots into the mod folder.
    internal class MenuTest : MonoBehaviour
    {
        private string _dir;
        private int _slot;
        private bool _inDungeon;

        public static MenuTest Start(string modDir, int slot)
        {
            GameObject go = new GameObject("QMCoop_MenuTest");
            DontDestroyOnLoad(go);
            MenuTest test = go.AddComponent<MenuTest>();
            test._dir = modDir;
            test._slot = slot;
            test.StartCoroutine(test.MainMenuPart());
            return test;
        }

        public void OnDungeonStarted()
        {
            if (!_inDungeon)
            {
                _inDungeon = true;
                StartCoroutine(PausePart());
            }
        }

        private IEnumerator MainMenuPart()
        {
            yield return new WaitForSecondsRealtime(4f);
            // First launch shows "press any key" before the main menu.
            UI.Chain<MainMenuScreen>().HideAll().Show();
            yield return new WaitForSecondsRealtime(1f);
            CoopSubmenu menu = FindObjectOfType<MainMenuScreen>()?.GetComponent<CoopSubmenu>();
            yield return Shot("menu_main_root.png");
            Report("main menu has co-op button: " + (menu != null));
            if (menu != null)
            {
                menu.Open();
                yield return new WaitForSecondsRealtime(1.5f);
                Report("main menu submenu: " + Captions(menu));
                yield return Shot("menu_main.png");
                menu.Close();
            }
            yield return new WaitForSecondsRealtime(0.5f);
            AccessTools.Method(typeof(GameModeStateMachine), "MainMenuFinished").Invoke(FindObjectOfType<GameModeStateMachine>(), new object[] { false, _slot, null });
        }

        private IEnumerator PausePart()
        {
            yield return new WaitForSecondsRealtime(4f);
            State state = CoopSession.State;
            Partner.Set(state, null);
            UI.Chain<EscScreen>().Show();
            yield return new WaitForSecondsRealtime(1f);
            CoopSubmenu menu = FindObjectOfType<EscScreen>()?.GetComponent<CoopSubmenu>();
            Report("pause menu has co-op button: " + (menu != null));
            if (menu != null)
            {
                menu.Open();
                yield return new WaitForSecondsRealtime(1.5f);
                Report($"pause submenu: {Captions(menu)}; party {CoopSession.Party.Count}");
                yield return Shot("menu_pause.png");

                CommonButton partner = menu.Items.FirstOrDefault(b => b.CaptionLabel.Label.StartsWith("Partner"));
                partner?.EvaluateClick();
                yield return new WaitForSecondsRealtime(1.5f);
                Report($"after partner click: {Captions(menu)}; party {CoopSession.Party.Count}; partner file '{Partner.Get(state)}'");
                yield return Shot("menu_pause_partner.png");
                menu.Close();
            }
            UI.Hide<EscScreen>();
            yield return new WaitForSecondsRealtime(1f);
            yield return Shot("menu_world.png");
            Partner.Set(state, null);
            Report("DONE");
            Application.Quit();
        }

        private static string Captions(CoopSubmenu menu)
        {
            return string.Join(" | ", menu.Items.Select(b => (b.IsInteractable ? "" : "(off) ") + b.captionText.text));
        }

        private IEnumerator Shot(string name)
        {
            yield return new WaitForEndOfFrame();
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, name), tex.EncodeToPNG());
            Destroy(tex);
        }

        private void Report(string line)
        {
            Log.Info("MENUTEST " + line);
            File.AppendAllText(Path.Combine(_dir, "menutest.log"), line + "\n");
        }
    }
}
