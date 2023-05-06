using Amazon.CDK;

namespace CarbotWebScraper
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();
            new CarbotWebScraperStack(app, "CarbotWebScraperStack", new StackProps());
            app.Synth();
        }
    }
}
