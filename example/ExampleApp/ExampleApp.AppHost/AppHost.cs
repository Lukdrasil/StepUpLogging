var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.SimpleApi>("simpleapi")
    .WithUrlForEndpoint("https", static url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .WithUrl("/scalar","Scalar")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.StepUpApi>("stepupapi")
    .WithUrlForEndpoint("https", static url => url.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .WithUrl("/scalar", "Scalar")
    .WithHttpHealthCheck("/health");

builder.Build().Run();
