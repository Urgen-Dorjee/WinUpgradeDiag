using WinUpgradeDiag.Core.Redaction;
using Xunit;

namespace WinUpgradeDiag.Tests.Redaction
{
    public class RedactorTests
    {
        private readonly Redactor _redactor = new Redactor("WS-TEST-0042", new[] { "jdoe", "jdoe-admin", "CONTOSO" });

        [Fact]
        public void Profile_path_username_is_replaced()
        {
            var result = _redactor.Redact(@"Migrating C:\Users\someoneelse\Documents\file.docx");

            Assert.Equal(@"Migrating C:\Users\<user>\Documents\file.docx", result);
        }

        [Fact]
        public void Non_personal_profiles_are_kept()
        {
            Assert.Equal(@"C:\Users\Public\Desktop", _redactor.Redact(@"C:\Users\Public\Desktop"));
            Assert.Equal(@"C:\Users\Default\NTUSER.DAT", _redactor.Redact(@"C:\Users\Default\NTUSER.DAT"));
        }

        [Fact]
        public void Known_user_and_domain_tokens_are_replaced_case_insensitively()
        {
            var result = _redactor.Redact(@"Logon by contoso\JDOE succeeded");

            Assert.Equal(@"Logon by <user>\<user> succeeded", result);
        }

        [Fact]
        public void Longer_username_is_replaced_whole_not_split()
        {
            Assert.Equal("user=<user>", _redactor.Redact("user=jdoe-admin"));
        }

        [Fact]
        public void Machine_name_is_replaced()
        {
            Assert.Equal("Computer: <machine>.", _redactor.Redact("Computer: ws-test-0042."));
        }

        [Fact]
        public void Tokens_inside_other_words_are_left_alone()
        {
            Assert.Equal("jdoesmith", _redactor.Redact("jdoesmith"));
        }

        [Fact]
        public void Null_and_empty_pass_through()
        {
            Assert.Null(_redactor.Redact(null));
            Assert.Equal("", _redactor.Redact(""));
        }

        // ---- redaction must not destroy the evidence the report exists to carry ----

        [Fact]
        public void A_profile_folder_named_after_a_panther_component_does_not_shred_the_log_text()
        {
            // MIG is a real Panther component column (DESIGN.md §4.3). If a machine happens to
            // have a profile folder called MIG, every component column in the report would
            // otherwise be rewritten to <user>.
            var redactor = new Redactor("WS-TEST-0042", new[] { "MIG", "SP", "CONX" });

            var line = redactor.Redact("2026-09-18 12:10:17, Error  MIG  Failure in SP during CONX phase");

            Assert.Equal("2026-09-18 12:10:17, Error  MIG  Failure in SP during CONX phase", line);
        }

        [Fact]
        public void Very_short_profile_names_are_not_treated_as_global_tokens()
        {
            // "IT" appearing mid-sentence must survive; rewriting two letters everywhere is
            // far more damaging than leaving a two-letter account name in place.
            var redactor = new Redactor("PC1", new[] { "IT", "sv" });

            Assert.Equal("The IT team reported sv errors on PC1",
                redactor.Redact("The IT team reported sv errors on PC1"));
        }

        [Fact]
        public void A_short_profile_name_is_still_redacted_inside_a_profile_path()
        {
            // Declining to treat "IT" as a global token must not leak C:\Users\IT\...
            var redactor = new Redactor("WS-TEST-0042", new[] { "IT" });

            Assert.Equal(@"C:\Users\<user>\Desktop\notes.txt",
                redactor.Redact(@"C:\Users\IT\Desktop\notes.txt"));
        }

        [Fact]
        public void Common_log_words_are_never_redacted_even_if_a_profile_matches_them()
        {
            var redactor = new Redactor("WS-TEST-0042", new[] { "Administrator", "Error", "System" });

            var line = redactor.Redact("Error: System reported a failure to Administrator");

            Assert.Equal("Error: System reported a failure to Administrator", line);
        }

        [Fact]
        public void A_real_account_name_of_the_minimum_length_is_still_redacted()
        {
            var redactor = new Redactor("WS-TEST-0042", new[] { "asmi" });

            Assert.Equal("user=<user>", redactor.Redact("user=asmi"));
        }
    }
}
