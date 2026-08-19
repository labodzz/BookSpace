# Infrastructure

This project contains replaceable technical implementations only: persistence,
tenant resolution, token/session storage, email delivery and external APIs.

Keep EF Core mappings, database tenant-isolation policies, transactional booking
constraints, refresh-token persistence and outbox delivery here. Application and
Domain must not reference this project.
