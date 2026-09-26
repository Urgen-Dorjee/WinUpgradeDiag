using WinUpgradeDiag.Core.Collect;
using Xunit;

namespace WinUpgradeDiag.Tests.Collect
{
    /// <summary>
    /// Windows 11 never updated <c>ProductName</c> in the registry. A real Windows 11 25H2 machine
    /// reports "Windows 10 Pro" with build 26200, so trusting that string labels every Windows 11
    /// endpoint as Windows 10 — in a tool whose entire job is a Windows 10 to 11 upgrade.
    /// </summary>
    public class OsIdentityTests
    {
        [Fact]
        public void A_real_windows_11_machine_is_not_reported_as_windows_10()
        {
            // Exactly what the registry returns on the author's Windows 11 25H2 machine.
            var os = new OsIdentity
            {
                ProductName = "Windows 10 Pro",
                EditionId = "Professional",
                DisplayVersion = "25H2",
                CurrentBuildNumber = "26200",
                Ubr = "9457"
            };

            Assert.True(os.IsWindows11);
            Assert.Equal("Windows 11 Pro", os.DisplayName);
            Assert.Equal("Windows 11 Pro 25H2 (build 26200.9457)", os.FullDescription);

            // The raw registry value is preserved, not overwritten.
            Assert.Equal("Windows 10 Pro", os.ProductName);
        }

        [Fact]
        public void A_genuine_windows_10_machine_is_left_alone()
        {
            var os = new OsIdentity
            {
                ProductName = "Windows 10 Enterprise",
                DisplayVersion = "22H2",
                CurrentBuildNumber = "19045",
                Ubr = "5011"
            };

            Assert.False(os.IsWindows11);
            Assert.Equal("Windows 10 Enterprise", os.DisplayName);
            Assert.Equal("Windows 10 Enterprise 22H2 (build 19045.5011)", os.FullDescription);
        }

        [Theory]
        [InlineData("21996", false)] // pre-release, below the cutoff
        [InlineData("22000", true)]  // first Windows 11 build
        [InlineData("22631", true)]  // 23H2
        [InlineData("26200", true)]  // 25H2
        [InlineData("19045", false)] // Windows 10 22H2
        public void The_build_number_decides_not_the_product_name(string build, bool expectedWindows11)
        {
            var os = new OsIdentity { ProductName = "Windows 10 Pro", CurrentBuildNumber = build };

            Assert.Equal(expectedWindows11, os.IsWindows11);
        }

        [Fact]
        public void Windows_server_is_never_relabelled_as_windows_11()
        {
            // Server builds can exceed the Windows 11 cutoff; the rename must not apply to them.
            var os = new OsIdentity
            {
                ProductName = "Windows Server 2025 Standard",
                CurrentBuildNumber = "26100"
            };

            Assert.False(os.IsWindows11);
            Assert.Equal("Windows Server 2025 Standard", os.DisplayName);
        }

        [Fact]
        public void An_unreadable_build_number_does_not_claim_windows_11()
        {
            var os = new OsIdentity { ProductName = "Windows 10 Pro", CurrentBuildNumber = null };

            Assert.False(os.IsWindows11);
            Assert.Equal("Windows 10 Pro", os.DisplayName);
        }

        [Fact]
        public void A_missing_product_name_still_yields_something_useful()
        {
            var os = new OsIdentity { CurrentBuildNumber = "26200" };

            Assert.Equal("Windows 11", os.DisplayName);
        }

        [Fact]
        public void An_entirely_empty_identity_reports_nothing_rather_than_guessing()
        {
            var os = new OsIdentity();

            Assert.Null(os.DisplayName);
            Assert.Null(os.FullDescription);
            Assert.Null(os.Build);
        }

        [Fact]
        public void A_build_without_a_ubr_still_reads_cleanly()
        {
            var os = new OsIdentity
            {
                ProductName = "Windows 10 Pro",
                DisplayVersion = "24H2",
                CurrentBuildNumber = "26100"
            };

            Assert.Equal("Windows 11 Pro 24H2 (build 26100)", os.FullDescription);
        }
    }
}
