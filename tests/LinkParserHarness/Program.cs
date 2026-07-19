using Anarise;

var link = Console.In.ReadToEnd().Trim();
if (string.IsNullOrEmpty(link))
{
    Console.Error.WriteLine("A share link is required on standard input.");
    return 2;
}

Console.Write(LinkParser.Parse(link));
return 0;
