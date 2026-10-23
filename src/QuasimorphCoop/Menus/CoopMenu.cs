using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MGSC;
using QuasimorphCoop.Net;
using UnityEngine;

namespace QuasimorphCoop.Menus
{
    // Adds a "Co-op" button to the pause menu (ship and missions) and the main menu. Clicking it
    // swaps the menu's buttons for a co-op submenu made of clones of the game's own menu buttons,
    // so it looks and behaves like the rest of the menu. Both menus are a VerticalLayoutGroup of
    // CommonButtons; button captions go through localization, and unknown keys come back unchanged.
    internal class CoopSubmenu : MonoBehaviour
    {
        public enum Context { Pause, MainMenu }

        private sealed class Entry
        {
            public string Label;
            public bool Enabled = true;
            public Action OnClick;
        }

        private Context _context;
        private CommonButton _template;
        private Transform _container;
        private readonly List<GameObject> _hidden = new List<GameObject>();
        private readonly List<CommonButton> _items = new List<CommonButton>();

        internal IReadOnlyList<CommonButton> Items => _items;
        private string _signature;
        private float _nextRefresh;
        private bool _open;

        public static void Attach(Component screen, CommonButton template, CommonButton insertBefore, Context context)
        {
            if (template == null || screen.GetComponent<CoopSubmenu>() != null)
            {
                return;
            }
            CoopSubmenu menu = screen.gameObject.AddComponent<CoopSubmenu>();
            menu._context = context;
            menu._template = template;
            menu._container = template.transform.parent;
            CommonButton open = menu.Clone("Co-op", menu.Open);
            open.name = "QMCoop_Open";
            if (insertBefore != null)
            {
                open.transform.SetSiblingIndex(insertBefore.transform.GetSiblingIndex());
            }
        }

        private CommonButton Clone(string label, Action onClick)
        {
            CommonButton button = Instantiate(_template, _container);
            button.name = "QMCoop_Item";
            button.gameObject.SetActive(true);
            button.SetInteractable(true);
            button.ChangeLabel(label);
            button.OnClick += (b, clicks) =>
            {
                try
                {
                    onClick?.Invoke();
                }
                catch (Exception e)
                {
                    Log.Error("Co-op menu action failed: " + e);
                }
            };
            return button;
        }

        internal void Open()
        {
            if (_open)
            {
                return;
            }
            _open = true;
            _hidden.Clear();
            foreach (Transform child in _container)
            {
                if (child.gameObject.activeSelf)
                {
                    _hidden.Add(child.gameObject);
                    child.gameObject.SetActive(false);
                }
            }
            _signature = null;
            Refresh();
        }

        internal void Close()
        {
            if (!_open)
            {
                return;
            }
            _open = false;
            ClearItems();
            foreach (GameObject go in _hidden)
            {
                if (go != null)
                {
                    go.SetActive(true);
                }
            }
            _hidden.Clear();
        }

        private void ClearItems()
        {
            foreach (CommonButton item in _items)
            {
                if (item != null)
                {
                    Destroy(item.gameObject);
                }
            }
            _items.Clear();
        }

        private void OnDisable()
        {
            Close();
        }

        private void Update()
        {
            if (_open && Time.unscaledTime >= _nextRefresh)
            {
                Refresh();
            }
        }

        // Rebuilds the buttons only when what they'd show has changed, so hover state isn't lost.
        private void Refresh()
        {
            _nextRefresh = Time.unscaledTime + 0.5f;
            List<Entry> entries = _context == Context.Pause ? PauseEntries() : MainMenuEntries();
            string signature = string.Join("|", entries.Select(e => e.Label + (e.Enabled ? "" : "#")));
            if (signature == _signature)
            {
                return;
            }
            _signature = signature;
            ClearItems();
            foreach (Entry e in entries)
            {
                CommonButton button = Clone(e.Label, e.OnClick);
                button.SetInteractable(e.Enabled);
                _items.Add(button);
            }
        }

        private List<Entry> PauseEntries()
        {
            State state = NetSession.GetGlobalState();
            List<Entry> entries = new List<Entry>
            {
                new Entry { Label = "Co-op: " + NetSession.Summary, Enabled = false }
            };

            if (state?.Get<SavedGameMetadata>() != null)
            {
                string partnerId = Partner.Get(state);
                Mercenary partner = partnerId == null ? null : state.Get<Mercenaries>().Get(partnerId);
                string partnerName = partner == null ? "none" : ShortName(partner);
                entries.Add(new Entry
                {
                    Label = "Partner: " + partnerName,
                    Enabled = !NetSession.Engaged,
                    OnClick = () => CyclePartner(state)
                });
            }

            if (!NetSession.Active)
            {
                entries.Add(new Entry { Label = "Host online", Enabled = SteamNet.SteamReady, OnClick = () => NetSession.Notify("Co-op: " + NetSession.StartHosting()) });
            }
            else if (NetSession.IsHost)
            {
                entries.Add(new Entry { Label = "Invite friend", OnClick = NetSession.InviteFriend });
            }
            if (NetSession.Active)
            {
                entries.Add(new Entry { Label = "Leave co-op", OnClick = () => NetSession.Leave("left from the menu") });
            }
            entries.Add(new Entry { Label = "Back", OnClick = Close });
            return entries;
        }

        private List<Entry> MainMenuEntries()
        {
            List<Entry> entries = new List<Entry>
            {
                new Entry { Label = "Co-op: " + NetSession.Summary, Enabled = false }
            };
            if (!NetSession.Active)
            {
                List<KeyValuePair<string, Steamworks.CSteamID>> hosts = SteamNet.Instance?.FriendsHosting() ?? new List<KeyValuePair<string, Steamworks.CSteamID>>();
                if (hosts.Count == 0)
                {
                    entries.Add(new Entry { Label = "No friends hosting", Enabled = false });
                }
                foreach (KeyValuePair<string, Steamworks.CSteamID> host in hosts)
                {
                    Steamworks.CSteamID lobby = host.Value;
                    entries.Add(new Entry { Label = "Join " + host.Key, OnClick = () => SteamNet.Instance.JoinLobby(lobby) });
                }
            }
            else
            {
                entries.Add(new Entry { Label = "Leave co-op", OnClick = () => NetSession.Leave("left from the menu") });
            }
            entries.Add(new Entry { Label = "Back", OnClick = Close });
            return entries;
        }

        // none -> first candidate -> ... -> last -> none
        private static void CyclePartner(State state)
        {
            List<Mercenary> candidates = Partner.Candidates(state);
            string current = Partner.Get(state);
            int index = candidates.FindIndex(m => m.ProfileId == current);
            Mercenary next = index + 1 < candidates.Count ? candidates[index + 1] : null;
            Partner.Set(state, next?.ProfileId);

            if (!CoopSession.InDungeon)
            {
                return;
            }
            if (CoopSession.Party.Count == 1)
            {
                Partner.AutoAdd();
                NetSession.HostSyncIfPossible();
            }
            else
            {
                NetSession.Notify("Co-op: the new partner joins from the next level on.");
            }
        }

        // Button width is limited; the first word of the merc's name is enough to tell them apart.
        private static string ShortName(Mercenary mercenary)
        {
            return CoopSession.DisplayName(mercenary).Split(' ')[0];
        }
    }

    [HarmonyPatch(typeof(EscScreen), "OnEnable")]
    internal static class EscScreenCoopButtonPatch
    {
        private static void Postfix(EscScreen __instance)
        {
            CoopSubmenu.Attach(__instance,
                Traverse.Create(__instance).Field("_settingsBtn").GetValue<CommonButton>(),
                Traverse.Create(__instance).Field("_toMainMenuBtn").GetValue<CommonButton>(),
                CoopSubmenu.Context.Pause);
        }
    }

    [HarmonyPatch(typeof(MainMenuScreen), "OnEnable")]
    internal static class MainMenuCoopButtonPatch
    {
        private static void Postfix(MainMenuScreen __instance)
        {
            CoopSubmenu.Attach(__instance,
                Traverse.Create(__instance).Field("_settingsBtn").GetValue<CommonButton>(),
                Traverse.Create(__instance).Field("_settingsBtn").GetValue<CommonButton>(),
                CoopSubmenu.Context.MainMenu);
        }
    }
}
