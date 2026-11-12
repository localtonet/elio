// Serves wwwroot as plain files for a local preview. The site itself is static: upload the contents of wwwroot to any web host.
var app = WebApplication.CreateBuilder(args).Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Anything that is not a file gets the page a static host would be set up to show for it.
app.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(Path.Combine(app.Environment.WebRootPath, "404.html"));
});

app.Run();
