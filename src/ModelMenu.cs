using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace EcstasyEngineer.AvatarReplacement
{
    // Adds an "Avatar model" entry to the game's Shapeshifting menu and owns the picker popup.
    // Called by Plugin.Update. Owns only objects it creates; no game callbacks are copied.
    internal sealed class ModelMenu : IDisposable
    {
        private const string RootName = "Pre-Game";
        private const string MenuPath = "MainMenu/Camera Canvas/MenuManager/VisualsMenu";
        private const string ButtonName = "Avatar model / EcstasyEngineerAvatarModel";
        private const int PageSize = 6;
        private readonly ModelCatalog catalog;
        private readonly Action<ModelCatalog.Entry> select;
        private readonly Func<string> status;
        private readonly ManualLogSource log;
        private Transform menu, nativeButton, nativeCollider;
        private RectTransform overlay, popup;
        private Canvas[] nativeCanvases = Array.Empty<Canvas>();
        private Text statusText, pageText;
        private Button previous, next;
        private readonly List<Button> rows = new List<Button>();
        private Font font;
        private int page;
        private float nextLookup;
        private bool disposed;
        private string selectionError, lastAttachmentError;
        internal Action DebugPanelRequested { get; set; }
        internal bool IsContextVisible => menu && menu.gameObject.activeInHierarchy &&
            nativeCanvases.Length != 0 && nativeCanvases.All(c => c && c.enabled && c.gameObject.activeInHierarchy);

        public ModelMenu(string bepRoot, Action<ModelCatalog.Entry> select, Func<string> status, ManualLogSource log)
        {
            this.select = select ?? throw new ArgumentNullException(nameof(select));
            this.status = status ?? (() => "");
            this.log = log;
            catalog = new ModelCatalog(bepRoot, log);
        }

        public void Tick()
        {
            if (disposed) return;
            try
            {
                if (!menu || !overlay)
                {
                    // Direct path lookup only; scanning every UI object each second caused visible hitches.
                    if (Time.unscaledTime < nextLookup) return;
                    nextLookup = Time.unscaledTime + 1;
                    var root = GameObject.Find("/" + RootName);
                    var found = root ? root.transform.Find(MenuPath) : null;
                    if (!found) { DestroyOwned(); return; }
                    DestroyOwned(); Attach(found);
                }
                UpdateVisibility();
                if (IsContextVisible && nativeCollider && Input.GetMouseButtonDown(0) && Picked(nativeCollider)) Open();
                if (popup && popup.gameObject.activeInHierarchy && Time.frameCount % 30 == 0) UpdateStatus();
            }
            catch (Exception error)
            {
                DestroyOwned(); AttachmentError("Avatar model menu unavailable: " + Short(error.Message, 220));
            }
        }

        // Mirrors the native buttons' PlayMaker MousePickEvent: a main-camera ray within 100 units.
        private static bool Picked(Transform target)
        {
            var camera = Camera.main;
            if (!camera) return false;
            return Physics.Raycast(camera.ScreenPointToRay(Input.mousePosition), out var hit, 100) && hit.collider && hit.collider.transform == target;
        }

        private void Attach(Transform found)
        {
            menu = found;
            nativeCanvases = found.GetComponentsInParent<Canvas>(true);
            if (nativeCanvases.Length == 0) throw new InvalidOperationException("Shapeshifting menu has no parent Canvas.");
            font = UiKit.FindFont(found);
            overlay = UiKit.Overlay("EcstasyEngineer.AvatarModelMenu", 30000, found.gameObject.layer);
            overlay.GetComponent<Canvas>().targetDisplay = nativeCanvases[0].targetDisplay;
            try { nativeButton = InjectNativeButton(found); nativeCollider = nativeButton.Find("Collider"); }
            catch (Exception error)
            {
                // Without a recognizable native template, fall back to a plain overlay button.
                log?.LogWarning("Native Avatar model button unavailable, using overlay button: " + error.Message);
                var open = UiKit.Button(font, "Open", "Avatar model", overlay, 0, 0, 220, 48, Open);
                var rect = (RectTransform)open.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1); rect.anchoredPosition = new Vector2(-24, -160);
            }
            BuildPopup();
            UpdateVisibility();
            lastAttachmentError = null;
            log?.LogInfo("Avatar model entry attached to " + HierarchyPaths.Full(found));
        }

        // Clones the lowest "Label / Event" button. Its native ButtonText FSM labels it from the
        // GameObject name; the Collider FSM is removed so the click never reaches the game's MenuManager.
        private Transform InjectNativeButton(Transform parent)
        {
            var buttons = Enumerable.Range(0, parent.childCount).Select(parent.GetChild)
                .Where(c => c.name.Contains(" / ") && c.name != ButtonName && c.gameObject.activeSelf && c.Find("Collider"))
                .OrderByDescending(c => c.localPosition.y).ToArray();
            if (buttons.Length == 0) throw new InvalidOperationException("No native menu button template found.");
            var template = buttons[buttons.Length - 1];
            var holder = new GameObject("EcstasyEngineer.ButtonTemplate");
            holder.SetActive(false);
            try
            {
                // Instantiated under an inactive holder so no cloned behaviour runs before it is stripped.
                var clone = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false).transform;
                clone.name = ButtonName;
                foreach (var child in clone.Cast<Transform>().Where(c => c.name.StartsWith("?", StringComparison.Ordinal)).ToArray())
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                var collider = clone.Find("Collider");
                foreach (var behaviour in collider.GetComponents<MonoBehaviour>().Where(b => b && b.GetType().Name == "PlayMakerFSM").ToArray())
                    UnityEngine.Object.DestroyImmediate(behaviour);
                clone.SetParent(parent, false);
                clone.SetSiblingIndex(template.GetSiblingIndex() + 1);
                var step = buttons.Length >= 2 ? template.localPosition - buttons[buttons.Length - 2].localPosition : new Vector3(0, -((RectTransform)template).rect.height, 0);
                clone.localPosition = template.localPosition + step;
                log?.LogInfo("Native Avatar model button cloned from '" + template.name + "' at " + clone.localPosition.ToString("F1") +
                    "; step " + step.ToString("F1") + "; " + buttons.Length + " native buttons.");
                return clone;
            }
            finally { UnityEngine.Object.Destroy(holder); }
        }

        private void BuildPopup()
        {
            // Left side, away from the native buttons: native colliders still receive clicks under this overlay.
            popup = UiKit.Rect("Picker", overlay);
            popup.anchorMin = popup.anchorMax = popup.pivot = new Vector2(0, .5f);
            popup.sizeDelta = new Vector2(660, 540); popup.anchoredPosition = new Vector2(60, 0);
            popup.gameObject.AddComponent<Image>().color = UiKit.PanelColor;
            UiKit.Label(font, "Title", "Avatar model", popup, 24, 16, 612, 36, 26);
            UiKit.Button(font, "Original", "Original (game model)", popup, 24, 62, 612, 40, () => Choose(null));
            for (var i = 0; i < PageSize; i++) rows.Add(UiKit.Button(font, "Model" + i, "", popup, 24, 110 + i * 44, 612, 40, null));
            previous = UiKit.Button(font, "Previous", "Previous", popup, 24, 380, 110, 34, () => { if (page > 0) { page--; RenderPage(); } });
            next = UiKit.Button(font, "Next", "Next", popup, 526, 380, 110, 34, () => { if ((page + 1) * PageSize < catalog.Entries.Count) { page++; RenderPage(); } });
            pageText = UiKit.Label(font, "Page", "", popup, 145, 380, 370, 34, 17); pageText.alignment = TextAnchor.MiddleCenter;
            statusText = UiKit.Label(font, "Status", "", popup, 24, 422, 612, 50, 13);
            UiKit.Label(font, "Note", "Drop VRM 0.x or 1.0 files in BepInEx/avatars. FBX bundles: experimental faces.", popup, 24, 476, 612, 22, 12);
            UiKit.Button(font, "Refresh", "Refresh", popup, 24, 503, 140, 30, Refresh);
            UiKit.Button(font, "DebugPanel", "Debug panel", popup, 230, 503, 200, 30, () => { popup.gameObject.SetActive(false); DebugPanelRequested?.Invoke(); });
            UiKit.Button(font, "Close", "Close", popup, 496, 503, 140, 30, () => popup.gameObject.SetActive(false));
            popup.gameObject.SetActive(false);
        }

        private void Open()
        {
            if (!popup) return;
            popup.gameObject.SetActive(true); popup.SetAsLastSibling(); Refresh();
        }

        private void Refresh()
        {
            selectionError = null; catalog.Refresh(); page = 0; RenderPage();
        }

        private void RenderPage()
        {
            var pages = Math.Max(1, (catalog.Entries.Count + PageSize - 1) / PageSize);
            page = Math.Max(0, Math.Min(page, pages - 1));
            for (var i = 0; i < rows.Count; i++)
            {
                var button = rows[i]; button.onClick.RemoveAllListeners();
                var index = page * PageSize + i;
                button.gameObject.SetActive(index < catalog.Entries.Count);
                if (index >= catalog.Entries.Count) continue;
                var entry = catalog.Entries[index];
                button.GetComponentInChildren<Text>().text = entry.Name + " [" + entry.SupportLabel + "]";
                button.onClick.AddListener(() => Choose(entry));
            }
            previous.interactable = page > 0; next.interactable = page + 1 < pages;
            pageText.text = catalog.Entries.Count == 0 ? "No local models" : "Page " + (page + 1) + " / " + pages;
            UpdateStatus();
        }

        private void Choose(ModelCatalog.Entry entry)
        {
            try { select(entry); selectionError = null; }
            catch (Exception error)
            {
                selectionError = "Selection failed: " + Short(error.Message, 220);
                log?.LogWarning("Avatar model menu: " + selectionError);
            }
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (!statusText) return;
            string current;
            try { current = Short(status() ?? "", 240); }
            catch (Exception error) { current = "Status unavailable: " + Short(error.Message, 120); }
            var problem = selectionError ?? (catalog.Errors.Count == 0 ? null : "Catalog: " + catalog.Errors[0] + (catalog.Errors.Count > 1 ? " (" + catalog.Errors.Count + " errors; see log)" : ""));
            statusText.text = current + (string.IsNullOrEmpty(problem) ? "" : "\n" + Short(problem, 280));
        }

        private static string Short(string value, int length) => new string(value.Where(c => !char.IsControl(c)).Take(length).ToArray());
        private void AttachmentError(string value)
        {
            if (value == lastAttachmentError) return;
            lastAttachmentError = value; log?.LogWarning(value);
        }
        private void UpdateVisibility()
        {
            if (!overlay) return;
            var visible = IsContextVisible;
            if (overlay.gameObject.activeSelf != visible)
            {
                // Reopening the native menu starts with the picker closed.
                if (!visible && popup) popup.gameObject.SetActive(false);
                overlay.gameObject.SetActive(visible);
            }
        }
        private void DestroyOwned()
        {
            if (overlay) { overlay.gameObject.SetActive(false); UnityEngine.Object.Destroy(overlay.gameObject); }
            if (nativeButton) UnityEngine.Object.Destroy(nativeButton.gameObject);
            menu = null; nativeButton = null; nativeCollider = null; overlay = null; popup = null; statusText = null; pageText = null;
            previous = null; next = null; rows.Clear(); font = null;
            nativeCanvases = Array.Empty<Canvas>();
        }
        public void Dispose() { if (disposed) return; disposed = true; DestroyOwned(); }
    }
}
