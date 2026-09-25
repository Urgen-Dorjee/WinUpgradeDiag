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
    }
}
