namespace WebScraper.Models;

// Root myDeserializedClass = JsonConvert.DeserializeObject<Root>(myJsonResponse);
public class Images
{
    public Small small { get; set; }
    public Large large { get; set; }
}

public class Item
{
    public int id { get; set; }
    public string url { get; set; }
    public string title { get; set; }
    public string subtitle { get; set; }
    public Images images { get; set; }
}

public class Large
{
    public int height { get; set; }
    public int width { get; set; }
    public string url { get; set; }
}

public class BatKeywordPage
{
    public int page_current { get; set; }
    public int page_maximum { get; set; }
    public int total { get; set; }
    public List<Item> items { get; set; }
}

public class Small
{
    public int height { get; set; }
    public int width { get; set; }
    public string url { get; set; }
}
