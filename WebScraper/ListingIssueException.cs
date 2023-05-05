namespace WebScraper;

public class ListingIssueException : Exception
{
    // This exception is thrown when the auction listing is not in the expected format or has some other problem
    public ListingIssueException()
    {
    }

    public ListingIssueException(string message)
        : base(message)
    {
    }

    public ListingIssueException(string message, Exception inner)
        : base(message, inner)
    {
    }
}