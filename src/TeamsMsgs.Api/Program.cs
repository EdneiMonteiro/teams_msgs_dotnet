// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();
