using System;
using System.Collections.Generic;
using System.Linq;

namespace ZeroAD.Sim.Content
{
    public static class EntityClassHelper
    {
        public static List<string> ParseClassTokens(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();
            return raw.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static List<string> BuildClassList(string classes, string visibleClasses, string? genericName = null)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in ParseClassTokens(classes))
                result.Add(c);
            foreach (var c in ParseClassTokens(visibleClasses))
                result.Add(c);
            if (!string.IsNullOrWhiteSpace(genericName))
                result.Add(genericName);
            return result.ToList();
        }

        /// <summary>
        /// Port of MatchesClassList from Templates.js.
        /// </summary>
        public static bool MatchesClassList(IReadOnlyList<string> classes, string match)
        {
            if (classes == null || classes.Count == 0 || string.IsNullOrWhiteSpace(match))
                return false;

            var groups = match.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var group in groups)
            {
                var tokens = group.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.All(c =>
                    (c.StartsWith('!') && !classes.Contains(c[1..])) ||
                    (!c.StartsWith('!') && classes.Contains(c))))
                    return true;
            }
            return false;
        }

        public static bool EntityMatchesClassList(IReadOnlyList<string> classes, string match) =>
            MatchesClassList(classes, match);
    }
}
