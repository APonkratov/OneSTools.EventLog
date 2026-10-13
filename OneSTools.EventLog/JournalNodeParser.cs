using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OneSTools.BracketsFile;

namespace OneSTools.EventLog
{
    // BracketsFile 2.1.9 assumes a quote is immediately followed by ',' or '}'.
    // Whitespace after a quoted value can cause unbounded recursion there.
    // Keep its public node model and raw doubled quotes; parse framing iteratively.
    internal static class JournalNodeParser
    {
        private sealed class Frame
        {
            public readonly BracketsNode Node = new BracketsNode();
            public bool ExpectValue = true;
        }

        public static BracketsNode Parse(StringBuilder source)
        {
            var stack = new Stack<Frame>();
            BracketsNode root = null;
            var index = 0;
            while (index < source.Length)
            {
                var ch = source[index];
                if (char.IsWhiteSpace(ch)) { index++; continue; }
                if (ch == '{')
                {
                    if (stack.Count >= 256) throw new InvalidDataException("Journal nesting exceeds 256 levels.");
                    var frame = new Frame();
                    if (stack.Count == 0)
                    {
                        if (root != null) throw new InvalidDataException("More than one journal root.");
                        root = frame.Node;
                    }
                    else
                    {
                        var parent = stack.Peek();
                        if (!parent.ExpectValue) throw new InvalidDataException("Missing journal separator.");
                        parent.Node.Nodes.Add(frame.Node); parent.ExpectValue = false;
                    }
                    stack.Push(frame); index++; continue;
                }
                if (stack.Count == 0) throw new InvalidDataException("Value outside journal root.");
                var current = stack.Peek();
                if (ch == '}')
                {
                    if (current.ExpectValue && current.Node.Count != 0) throw new InvalidDataException("Missing journal value.");
                    stack.Pop(); index++; continue;
                }
                if (ch == ',')
                {
                    if (current.ExpectValue) throw new InvalidDataException("Missing journal value.");
                    current.ExpectValue = true; index++; continue;
                }
                if (!current.ExpectValue) throw new InvalidDataException("Missing journal separator.");
                string value;
                if (ch == '"')
                {
                    var start = ++index;
                    var closed = false;
                    while (index < source.Length)
                    {
                        if (source[index] != '"') { index++; continue; }
                        if (index + 1 < source.Length && source[index + 1] == '"') { index += 2; continue; }
                        closed = true; break;
                    }
                    if (!closed) throw new InvalidDataException("Unclosed journal string.");
                    value = source.ToString(start, index - start); index++;
                }
                else
                {
                    var start = index;
                    while (index < source.Length && source[index] != ',' && source[index] != '}')
                    {
                        if (source[index] == '{' || source[index] == '"') throw new InvalidDataException("Invalid unquoted journal value.");
                        index++;
                    }
                    value = source.ToString(start, index - start).Trim();
                    if (value.Length == 0) throw new InvalidDataException("Empty unquoted journal value.");
                }
                current.Node.Nodes.Add(new BracketsNode(value)); current.ExpectValue = false;
            }
            if (root == null || stack.Count != 0) throw new InvalidDataException("Incomplete journal root.");
            return root;
        }
    }
}
