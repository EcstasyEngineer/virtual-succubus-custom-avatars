using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace EcstasyEngineer.AvatarReplacement
{
    internal static class HierarchyPaths
    {
        // Scene-root/child path; scene name excluded.
        internal static string Full(Transform value)
        {
            var path = value.name;
            while (value.parent) { value = value.parent; path = value.name + "/" + path; }
            return path;
        }

        // Name path below root, or "." for the root itself.
        internal static string Relative(Transform value, Transform root)
        {
            if (value == root) return ".";
            var segments = new List<string>();
            var current = value;
            while (current && current != root) { segments.Add(current.name); current = current.parent; }
            if (current != root)
                throw new ArgumentException("Transform is not a descendant of the supplied root.", nameof(value));
            segments.Reverse();
            return string.Join("/", segments);
        }

        // Exact within the observed hierarchy, including duplicate sibling names.
        // A rename or sibling reorder changes this path; recapture after hierarchy changes.
        internal static string IndexedRelative(Transform value, Transform root)
        {
            if (!value) throw new ArgumentNullException(nameof(value));
            if (!root) throw new ArgumentNullException(nameof(root));
            if (value == root) return ".";

            var segments = new List<string>();
            var current = value;
            while (current && current != root)
            {
                segments.Add(current.GetSiblingIndex().ToString(CultureInfo.InvariantCulture) + ":" + current.name);
                current = current.parent;
            }
            if (current != root)
                throw new ArgumentException("Transform is not a descendant of the supplied root.", nameof(value));
            segments.Reverse();
            return "@" + string.Join("/", segments);
        }
    }
}
