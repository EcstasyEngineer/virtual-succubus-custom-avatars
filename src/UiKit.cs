using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace EcstasyEngineer.AvatarReplacement
{
    // Shared builders for the plugin's own overlay canvases. Owns only objects it creates.
    internal static class UiKit
    {
        internal static readonly Color ButtonColor = new Color(.18f, .22f, .29f, 1);
        internal static readonly Color PanelColor = new Color(.06f, .07f, .1f, .98f);

        internal static Font FindFont(Transform near = null)
        {
            var font = near ? near.GetComponentsInChildren<Text>(true).Select(t => t.font).FirstOrDefault(f => f) : null;
            if (!font) font = Resources.FindObjectsOfTypeAll<Text>().Where(t => t && t.font).Select(t => t.font).FirstOrDefault();
            if (!font) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (!font) throw new InvalidOperationException("No usable UI font found.");
            return font;
        }

        internal static RectTransform Overlay(string name, int sortingOrder, int layer = 0)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            obj.layer = layer;
            UnityEngine.Object.DontDestroyOnLoad(obj);
            var canvas = obj.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = sortingOrder;
            var scaler = obj.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = .5f;
            return (RectTransform)obj.transform;
        }

        internal static RectTransform Rect(string name, Transform parent)
        {
            var obj = new GameObject(name, typeof(RectTransform)); obj.layer = parent.gameObject.layer;
            var rect = (RectTransform)obj.transform; rect.SetParent(parent, false); return rect;
        }

        // Top-left anchored placement in reference pixels.
        internal static void Place(RectTransform rect, float x, float y, float width, float height)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1); rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(x, -y); rect.sizeDelta = new Vector2(width, height);
        }

        internal static RectTransform Panel(string name, Transform parent, float x, float y, float width, float height)
        {
            var rect = Rect(name, parent); Place(rect, x, y, width, height);
            rect.gameObject.AddComponent<Image>().color = PanelColor;
            return rect;
        }

        internal static Text Label(Font font, string name, string content, Transform parent, float x, float y, float width, float height, int size)
        {
            var rect = Rect(name, parent); Place(rect, x, y, width, height);
            var text = rect.gameObject.AddComponent<Text>(); text.font = font; text.text = content;
            text.fontSize = size; text.color = Color.white; text.alignment = TextAnchor.UpperLeft;
            text.supportRichText = false; text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        // onError receives exceptions from the click handler instead of letting them reach the EventSystem.
        internal static Button Button(Font font, string name, string label, Transform parent, float x, float y, float width, float height,
            Action clicked, Action<Exception> onError = null)
        {
            var rect = Rect(name, parent); Place(rect, x, y, width, height);
            var image = rect.gameObject.AddComponent<Image>(); image.color = ButtonColor;
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var text = Label(font, "Label", label, rect, 8, 0, width - 16, height, 17);
            text.alignment = TextAnchor.MiddleCenter; text.resizeTextForBestFit = true; text.resizeTextMinSize = 11; text.resizeTextMaxSize = 17;
            if (clicked != null) button.onClick.AddListener(() =>
            {
                try { clicked(); }
                catch (Exception error) { if (onError != null) onError(error); else throw; }
            });
            return button;
        }
    }
}
