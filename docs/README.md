# DotNetBoost.MultiLingual documentation

New here? Start with the [5-minute quick start](../README.md#get-started-in-5-minutes).

**Setting up**
- [Declaring translatable content](translatable-content.md): `[MultiLingual]`, entity names, what a key has to look like
- [Storage providers](storage-providers.md): EF Core, Dapper, MongoDB
- [Reading and writing](reading-and-writing.md): `IMultiLingualManager`, what `null` means, deleting

**Features**
- [Translated queries](querying.md): filter, sort and project on translated text, in SQL
- [Base-language mode](base-language.md): keep one language in the entity's own columns
- [Languages](languages.md): the rules, the cache, several instances
- [REST API](rest-api.md): `Accept-Language`, the language endpoints, and **how to secure them**
- [Localized messages](localization.md): `IStringLocalizer` over JSON, in the content's language

**Providers**
- [Dapper](dapper.md): the EF Core schema, `migrateSchema`, one transaction per request
- [MongoDB](mongodb.md): two collections, class-map unmapping, sessions

**Tooling**
- [Dashboard (SPA client)](dashboard.md): a web UI for languages and content
- [Running everything with .NET Aspire](aspire.md): the full demo stack in one command

**Reference**
- [Configuration reference](configuration-reference.md): every `AddMultiLingual()` builder method
- [Known limitations](limitations.md): what this does not do, and what to do instead
- [Architecture](architecture.md): how the pieces fit, repo layout, running the tests
- [Adding a provider](adding-a-provider.md): what a storage backend has to implement
