using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.TextManager.Interop;

namespace Vistudio
{
    // Native Watch routes commit/navigation keys through the SDK completion shim.
    [ComVisible(true)]
    public sealed class KotlinNativeCompletionSet : IVsCompletionSet
    {
        private readonly List<Completion> entries;
        private readonly int line, start, end;
        internal Action? OnDismiss { get; set; }
        internal KotlinNativeCompletionSet(List<Completion> entries, int line, int start, int end)
        { this.entries = entries; this.line = line; this.start = start; this.end = end; }
        public int GetCount() => entries.Count;
        public uint GetFlags() => (uint)(UpdateCompletionFlags.CSF_INITIALEXTENTKNOWN | UpdateCompletionFlags.CSF_HAVEDESCRIPTIONS |
            UpdateCompletionFlags.CSF_NOCASEMATCHING | UpdateCompletionFlags.CSF_CUSTOMCOMMIT | UpdateCompletionFlags.CSF_CUSTOMMATCHING);
        public int GetImageList(out IntPtr images) { images = IntPtr.Zero; return VSConstants.S_OK; }
        public int GetDisplayText(int index, out string text, int[] glyph)
        { text = entries[index].DisplayText; if (glyph != null && glyph.Length > 0) glyph[0] = -1; return VSConstants.S_OK; }
        public int GetDescriptionText(int index, out string text) { text = entries[index].Description ?? ""; return VSConstants.S_OK; }
        public int GetInitialExtent(out int initialLine, out int initialStart, out int initialEnd)
        { initialLine = line; initialStart = start; initialEnd = end; return VSConstants.S_OK; }
        public int GetBestMatch(string text, int length, out int index, out uint flags)
        {
            index = entries.FindIndex(entry => entry.InsertionText.StartsWith(text.Substring(0, Math.Min(length, text.Length)), StringComparison.OrdinalIgnoreCase));
            flags = index >= 0 ? (uint)UpdateCompletionFlags.GBM_SELECT : 0;
            if (index < 0) index = 0;
            return VSConstants.S_OK;
        }
        public int OnCommit(string text, int index, int selected, ushort commitCharacter, out string completeWord)
        {
            completeWord = index >= 0 && index < entries.Count ? entries[index].InsertionText : text;
            // With CSF_CUSTOMCOMMIT the shim calls this for every typed character.
            // Keep filtering identifiers rather than committing on their first letter.
            var c = (char)commitCharacter;
            return char.IsLetterOrDigit(c) || c == '_' || c == '`' ? VSConstants.S_FALSE : VSConstants.S_OK;
        }
        public void Dismiss() => OnDismiss?.Invoke();
    }
}
