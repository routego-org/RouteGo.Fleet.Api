// Slice test files (linked in from {Project}/Features/**/*.Tests.cs) carry no using directives —
// this file holds the test project's shared set. The sibling slice skill EXTENDS it as its
// test helpers need more (e.g. Testcontainers, AngleSharp).
global using Xunit;
global using RouteGo.Fleet.Api.Common;             // HttpClients, WireJson
global using System.Net;                           // HttpStatusCode in slice tests
global using RouteGo.Fleet.Api.Tests;              // ApiFactory
global using RouteGo.Fleet.Api.Tests.Database;     // DatabaseFixture, DatabaseCollection
global using RouteGo.Fleet.Api.Tests.Support;      // FleetTestHost, TestAccessTokens
