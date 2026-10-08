#!/bin/sh
# Cria papéis, bancos e permissões (ADR-0001). Executado só na primeira inicialização do volume.
# Espelha src/Infrastructure/Persistence/TenantDatabaseBootstrap.cs — manter os dois iguais.
set -eu

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<SQL
CREATE ROLE app_migrator LOGIN NOBYPASSRLS PASSWORD '${APP_MIGRATOR_PASSWORD}';
CREATE ROLE app_user LOGIN NOBYPASSRLS NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '${APP_USER_PASSWORD}';
CREATE DATABASE plataforma OWNER app_migrator;
CREATE DATABASE lojas OWNER app_migrator;
SQL

for db in plataforma lojas; do
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$db" <<SQL
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrator IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
ALTER DEFAULT PRIVILEGES FOR ROLE app_migrator IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;
SQL
done
