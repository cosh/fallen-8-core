// MIT License
//
// EntitySummaryTemplate.cs
//
// Copyright (c) 2011-2026 Henning Rauch
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Text;
using NoSQL.GraphDB.Integrations.Validation;

namespace NoSQL.GraphDB.Integrations.Summary
{
    /// <summary>
    ///   Renders the text to embed for one entity from a DECLARATIVE template the provider's descriptor carries.
    ///
    ///   <para>Declarative and not code, deliberately: a template expressed as code would put provider-authored
    ///   code on the path that produces embedding text, and everything else about the boundary exists to keep the
    ///   provider away from decisions the runtime has to be able to review. So a template is a string with
    ///   <c>{placeholder}</c> holes, filled from the entity's OWN kind and properties and from nothing else.</para>
    ///
    ///   <para>A hole the entity cannot fill collapses, along with the punctuation left dangling around it,
    ///   because an absent value is absent: rendering "Fronius , VLAN , garage" would embed the shape of the
    ///   template rather than the description of the thing.</para>
    /// </summary>
    public static class EntitySummaryTemplate
    {
        /// <summary>The placeholder that stands for the entity's kind, which is not a property.</summary>
        public const String KindPlaceholder = "kind";

        /// <summary>
        ///   Renders one entity, or null when the template is empty or every hole collapsed.
        /// </summary>
        /// <param name="template">The descriptor's template, e.g. <c>{kind} {unifi.name}, {unifi.model}</c>.</param>
        /// <param name="entity">The validated entity, whose rendered property values are the only data used.</param>
        public static String? Render(String? template, ValidatedEntity entity)
        {
            if (String.IsNullOrWhiteSpace(template) || entity == null)
            {
                return null;
            }

            var text = new StringBuilder(template!.Length);
            var index = 0;

            while (index < template.Length)
            {
                var open = template.IndexOf('{', index);
                if (open < 0)
                {
                    text.Append(template, index, template.Length - index);
                    break;
                }

                var close = template.IndexOf('}', open + 1);
                if (close < 0)
                {
                    // An unclosed brace is literal text: refusing the template would turn a typo into a failed
                    // run, and there is nothing to guess.
                    text.Append(template, index, template.Length - index);
                    break;
                }

                text.Append(template, index, open - index);

                var key = template.Substring(open + 1, close - open - 1).Trim();
                text.Append(Lookup(entity, key));
                index = close + 1;
            }

            var rendered = Collapse(text.ToString());
            return rendered.Length == 0 ? null : rendered;
        }

        private static String Lookup(ValidatedEntity entity, String key)
        {
            if (String.Equals(key, KindPlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                return entity.Kind;
            }

            foreach (var property in entity.Properties)
            {
                if (String.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Text;
                }
            }

            return String.Empty;
        }

        /// <summary>
        ///   Tidies what an unfilled hole leaves behind: runs of whitespace become one space, punctuation with
        ///   nothing in front of it goes, and the ends are trimmed of separators.
        /// </summary>
        private static String Collapse(String rendered)
        {
            var text = new StringBuilder(rendered.Length);
            var pendingSpace = false;

            foreach (var character in rendered)
            {
                if (Char.IsWhiteSpace(character))
                {
                    pendingSpace = text.Length > 0;
                    continue;
                }

                if (character == ',' || character == ';')
                {
                    // A separator is only worth keeping when something precedes it that is not itself a separator.
                    if (text.Length == 0 || IsSeparator(text[text.Length - 1]))
                    {
                        pendingSpace = false;
                        continue;
                    }

                    text.Append(character);
                    pendingSpace = false;
                    continue;
                }

                if (pendingSpace)
                {
                    text.Append(' ');
                    pendingSpace = false;
                }

                text.Append(character);
            }

            while (text.Length > 0 && (IsSeparator(text[text.Length - 1]) || Char.IsWhiteSpace(text[text.Length - 1])))
            {
                text.Length--;
            }

            return text.ToString();
        }

        private static Boolean IsSeparator(Char character)
        {
            return character == ',' || character == ';';
        }
    }
}
