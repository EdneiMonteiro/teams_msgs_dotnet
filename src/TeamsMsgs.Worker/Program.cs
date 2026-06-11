// Copyright (c) 2026 Ednei Monteiro. Licensed under the MIT License.
// See LICENSE and DISCLAIMER.md in the project root for details.

using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
var app = builder.Build();
await app.RunAsync().ConfigureAwait(false);
