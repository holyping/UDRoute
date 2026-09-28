using System.Globalization;
using Xunit;

namespace UDRoute.Tests
{
    public class I18nTests
    {
        [Fact]
        public void I18n_StaticText_SelectsByCulture()
        {
            var origCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                Assert.Equal("你好", I18n.Text("你好", "Hello"));

                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                Assert.Equal("Hello", I18n.Text("你好", "Hello"));
            }
            finally
            {
                CultureInfo.CurrentUICulture = origCulture;
            }
        }

        [Fact]
        public void I18n_Format_SelectsAndFormatsByCulture()
        {
            var origCulture = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                Assert.Equal("端口: 8080", I18n.Format("端口: {0}", "Port: {0}", 8080));

                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                Assert.Equal("Port: 8080", I18n.Format("端口: {0}", "Port: {0}", 8080));
            }
            finally
            {
                CultureInfo.CurrentUICulture = origCulture;
            }
        }

        [Fact]
        public void I18n_Interpolated_ShortCircuitsInactiveLanguage()
        {
            var origCulture = CultureInfo.CurrentUICulture;
            try
            {
                int zhCalls = 0;
                int enCalls = 0;

                string ZhEval() { zhCalls++; return "ZH"; }
                string EnEval() { enCalls++; return "EN"; }

                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                string resZh = I18n.Text($"中文-{ZhEval()}", $"English-{EnEval()}");
                Assert.Equal("中文-ZH", resZh);
                Assert.Equal(1, zhCalls);
                Assert.Equal(0, enCalls); // EnEval was NEVER executed! Short-circuited!

                zhCalls = 0;
                enCalls = 0;

                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                string resEn = I18n.Text($"中文-{ZhEval()}", $"English-{EnEval()}");
                Assert.Equal("English-EN", resEn);
                Assert.Equal(0, zhCalls); // ZhEval was NEVER executed! Short-circuited!
                Assert.Equal(1, enCalls);
                // Mixed test
                CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
                Assert.Equal("中文-ZH", I18n.Text($"中文-{ZhEval()}", "English static"));
                Assert.Equal("中文静态", I18n.Text("中文静态", $"English-{EnEval()}"));

                CultureInfo.CurrentUICulture = new CultureInfo("en-US");
                Assert.Equal("English static", I18n.Text($"中文-{ZhEval()}", "English static"));
                Assert.Equal("English-EN", I18n.Text("中文静态", $"English-{EnEval()}"));
            }
            finally
            {
                CultureInfo.CurrentUICulture = origCulture;
            }
        }
    }
}
