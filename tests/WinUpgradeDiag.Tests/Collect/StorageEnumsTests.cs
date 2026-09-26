using WinUpgradeDiag.Core.Collect;
using Xunit;

namespace WinUpgradeDiag.Tests.Collect
{
    public class StorageEnumsTests
    {
        [Theory]
        [InlineData(0, "Healthy")]
        [InlineData(1, "Warning")]
        [InlineData(2, "Unhealthy")]
        public void Health_status_reads_as_a_word_not_a_number(int raw, string expected)
        {
            // HW-001 is the highest-precedence rule in docs/RULES.md and turns on this distinction.
            Assert.Equal(expected, StorageEnums.HealthStatus((ushort)raw));
        }

        [Fact]
        public void An_unknown_health_value_falls_back_to_the_number_rather_than_guessing()
        {
            Assert.Equal("77", StorageEnums.HealthStatus((ushort)77));
        }

        [Fact]
        public void Operational_status_array_is_joined_instead_of_printing_the_type_name()
        {
            // The provider returns UInt16[]; ToString() on it yields "System.UInt16[]".
            var value = new ushort[] { 2, 5 };

            var text = StorageEnums.OperationalStatus(value);

            Assert.Equal("OK, Predictive Failure", text);
            Assert.DoesNotContain("UInt16", text);
        }

        [Fact]
        public void A_single_operational_status_value_still_works()
        {
            Assert.Equal("OK", StorageEnums.OperationalStatus((ushort)2));
        }

        [Fact]
        public void Predictive_failure_is_named_so_a_dying_disk_is_not_missed()
        {
            Assert.Equal("Predictive Failure", StorageEnums.OperationalStatus(new ushort[] { 5 }));
        }

        [Fact]
        public void Vendor_specific_operational_codes_are_named()
        {
            Assert.Equal("Hardware Error", StorageEnums.OperationalStatus(new[] { 0xD011 }));
        }

        [Fact]
        public void An_empty_operational_array_is_null_rather_than_an_empty_string()
        {
            Assert.Null(StorageEnums.OperationalStatus(new ushort[0]));
        }

        [Fact]
        public void Nulls_pass_through_as_nulls()
        {
            Assert.Null(StorageEnums.HealthStatus(null));
            Assert.Null(StorageEnums.OperationalStatus(null));
            Assert.Null(StorageEnums.StartMode(null));
        }

        [Theory]
        [InlineData(0, "Boot")]
        [InlineData(1, "System")]
        [InlineData(2, "Automatic")]
        [InlineData(3, "Manual")]
        [InlineData(4, "Disabled")]
        public void Service_start_mode_reads_as_a_word(int raw, string expected)
        {
            Assert.Equal(expected, StorageEnums.StartMode(raw));
        }

        [Fact]
        public void Start_mode_name_is_exposed_on_the_filter_driver_itself()
        {
            var driver = new FilterDriverInfo { ServiceName = "secrmm", StartMode = 0 };

            Assert.Equal("Boot", driver.StartModeName);
        }

        [Fact]
        public void A_non_numeric_value_is_shown_as_given_rather_than_swallowed()
        {
            Assert.Equal("unexpected", StorageEnums.HealthStatus("unexpected"));
        }
    }
}
