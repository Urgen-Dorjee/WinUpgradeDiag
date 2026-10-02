using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WinUpgradeDiag.Core.Remediation;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// Checks the catalogue against the scripts it launches.
    /// <para>
    /// The catalogue is what the UI reads to decide whether to ask for anything, and the script is
    /// what actually runs. If a script gains a mandatory parameter the catalogue does not declare,
    /// the tool launches without it and PowerShell refuses with "Cannot process command because of
    /// one or more missing mandatory parameters" — a message that means nothing to a technician
    /// standing at a broken machine, arriving after they have already confirmed a destructive
    /// action. The reverse is just as bad: asking for a value the script does not take.
    /// </para>
    /// <para>
    /// Both are invisible until someone runs the tool, which is why this is asserted rather than
    /// reviewed by eye.
    /// </para>
    /// </summary>
    public class ScriptParameterContractTests
    {
        /// <summary>
        /// A parameter declared mandatory, in either spelling PowerShell accepts:
        /// <c>[Parameter(Mandatory)]</c> and <c>[Parameter(Mandatory = $true)]</c>.
        /// </summary>
        private static readonly Regex MandatoryParameter = new Regex(
            // The attribute, in either spelling.
            @"\[Parameter\([^\)]*Mandatory(?:\s*=\s*\$true)?[^\)]*\)\]" +
            // Then the first type declaration that follows it. Skipping validators by matching
            // bracket pairs does not work: [ValidatePattern('^[A-Za-z0-9]{3}$')] nests brackets
            // and contains a dollar sign, so both "balanced brackets" and "skip to the next $"
            // capture the wrong thing. Matching the type names directly cannot be fooled by either.
            @"[\s\S]*?\[(?:string|int|switch|bool|datetime|long|double)(?:\[\])?\]\s*\$(?<name>\w+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string ScriptText(string scriptName)
        {
            var bytes = EmbeddedScriptProvider.Read(scriptName);
            Assert.True(bytes != null, scriptName + " is not embedded in the assembly.");
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>
        /// Mandatory parameter names, de-duplicated. A script offering the same value under
        /// several parameter sets — as the client rebuild does with SiteCode/ManagementPoint
        /// versus Target — counts each name once.
        /// </summary>
        private static IReadOnlyList<string> MandatoryNames(string scriptName)
        {
            return MandatoryParameter.Matches(ScriptText(scriptName))
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [Fact]
        public void A_tool_that_declares_a_parameter_passes_one_its_script_accepts()
        {
            foreach (var tool in ToolCatalog.All.Where(t => t.RequiresParameter))
            {
                var declared = MandatoryNames(tool.ScriptName);

                Assert.True(
                    declared.Contains(tool.ParameterName, StringComparer.OrdinalIgnoreCase),
                    tool.Id + " passes -" + tool.ParameterName + " but " + tool.ScriptName +
                    " declares [" + string.Join(", ", declared) + "].");
            }
        }

        /// <summary>
        /// The failure that reaches a technician: a script needs a value, the catalogue does not
        /// know, so the tool is launched without it and PowerShell refuses after the confirmation
        /// dialog has already been accepted.
        /// </summary>
        [Fact]
        public void No_script_needs_a_value_its_tool_never_asks_for()
        {
            foreach (var tool in ToolCatalog.All)
            {
                var mandatory = MandatoryNames(tool.ScriptName);
                if (mandatory.Count == 0)
                {
                    continue;
                }

                Assert.True(tool.RequiresParameter,
                    tool.Id + " asks for nothing, but " + tool.ScriptName + " requires -" +
                    string.Join(" and -", mandatory) + ". It would fail the moment it ran.");

                // A script with several parameter sets satisfies this through any one of them;
                // what matters is that the value the tool supplies is among them.
                Assert.True(
                    mandatory.Contains(tool.ParameterName, StringComparer.OrdinalIgnoreCase),
                    tool.Id + " supplies -" + tool.ParameterName + ", which is not one of [" +
                    string.Join(", ", mandatory) + "].");
            }
        }

        /// <summary>
        /// Every parameter a technician is asked for needs a prompt, a worked example and help.
        /// An unlabelled box is the complaint that produced this test.
        /// </summary>
        [Fact]
        public void Every_prompt_says_what_it_wants_and_shows_an_example()
        {
            foreach (var tool in ToolCatalog.All.Where(t => t.RequiresParameter))
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.ParameterPrompt),
                    tool.Id + " has no prompt.");
                Assert.False(string.IsNullOrWhiteSpace(tool.ParameterExample),
                    tool.Id + " shows no example of the value.");
                Assert.False(string.IsNullOrWhiteSpace(tool.ParameterHelp),
                    tool.Id + " has no help under the field.");

                // "Enter a value" tells nobody anything.
                Assert.True(tool.ParameterPrompt.Length > 12,
                    tool.Id + " prompt is too short to say anything: " + tool.ParameterPrompt);
            }
        }

        /// <summary>
        /// Every embedded script belongs to a tool, or it is dead weight nobody can reach — and
        /// every tool's script is embedded, or the tool cannot run at all.
        /// </summary>
        [Fact]
        public void The_embedded_scripts_and_the_catalogue_agree()
        {
            var embedded = EmbeddedScriptProvider.AvailableScripts;
            var referenced = ToolCatalog.All.Select(t => t.ScriptName).Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var scriptName in referenced)
            {
                Assert.True(
                    embedded.Contains(scriptName, StringComparer.OrdinalIgnoreCase),
                    scriptName + " is referenced by a tool but not embedded.");
            }

            // Start-Here.ps1 is the standalone menu for running the scripts from a prompt without
            // the application. It is deliberately not a tool: the Tools tab is that menu.
            var unreachable = embedded
                .Where(s => !referenced.Contains(s, StringComparer.OrdinalIgnoreCase))
                .Where(s => !string.Equals(s, "Start-Here.ps1", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(unreachable.Count == 0,
                "Embedded but unreachable from the UI: " + string.Join(", ", unreachable));
        }

        /// <summary>
        /// The regex is doing real work, so prove it matches both spellings and ignores an
        /// optional parameter. A silently-matching-nothing pattern would pass every test above.
        /// </summary>
        [Fact]
        public void The_mandatory_parameter_pattern_actually_matches()
        {
            // Both spellings, a validator in between, and an optional parameter that must not match.
            const string sample =
                "param(\n" +
                "    [Parameter(Mandatory)]\n" +
                "    [string]$ContentId,\n" +
                "    [Parameter(ParameterSetName = 'Named', Mandatory = $true)]\n" +
                "    [ValidatePattern('^[A-Za-z0-9]{3}$')]\n" +
                "    [string] $SiteCode,\n" +
                "    [int] $Tail = 25\n" +
                ")";

            var found = MandatoryParameter.Matches(sample)
                .Cast<Match>()
                .Select(m => m.Groups["name"].Value)
                .ToList();

            Assert.Equal(new[] { "ContentId", "SiteCode" }, found);
        }
    }
}
