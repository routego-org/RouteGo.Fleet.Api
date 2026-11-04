// Slice files carry no using directives — this file holds the shared set.
// The sibling slice skill and database setup EXTEND this list beneath the '// FILLED BY' marker below.
global using System.Text.Json;          // JsonSerializer, WireJson.Options
global using Microsoft.AspNetCore.Mvc;
global using RouteGo.Fleet.Api.Common;             // HttpClients, WireJson
global using RouteGo.Fleet.Api.Common.Ports;       // IClock
global using RouteGo.Fleet.Api.Common.Problems;    // ProblemResult, ProblemTypes (catalog projection)
global using RouteGo.Fleet.Api.Common.Behaviors;   // UnhandledExceptionHandler, ResponseMetadataMiddleware, ResponseHeadersTransformer
// FILLED BY the sibling slice skill — and by skills-db-postgres (copy-assets.ps1) at database setup:
global using System.Data.Common;   // skills-db-postgres (copy-assets.ps1)
global using Dapper;   // skills-db-postgres (copy-assets.ps1)
global using Microsoft.AspNetCore.Authorization;   // [Authorize] / [AllowAnonymous] on slice controllers
global using FluentValidation;                     // slice validators
global using FluentValidation.Results;             // ValidationResult in the controller
global using RouteGo.Fleet.Api.Common.Security;    // Policies, TokenClaims
global using RouteGo.Fleet.Api.Domain.Buses;       // bus rules and enums shared by the bus slices
global using RouteGo.Fleet.Api.Domain.OperatorProfiles; // operator profile rules
global using RouteGo.Fleet.Api.Domain.Routes;      // route rules and status shared by the route slices
global using RouteGo.Fleet.Api.Domain.Trips;       // schedule and trip rules shared by the trip slices
