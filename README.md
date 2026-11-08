# GeoWoof · backend

API de GeoWoof, una red social para dueños de mascotas: paseos con GPS, paseantes en directo, seguidores, publicaciones y chat. La app Android está en [geowoof-and](https://github.com/eirikrg/geowoof-and).

.NET 10 (monolito modular), PostgreSQL + PostGIS, Redis, SignalR y almacenamiento S3 (SeaweedFS).

## Arrancar

```bash
docker compose up --build
```

La API queda en http://localhost:8080, con su documentación en `/scalar`, y los correos que envía en Mailpit (http://localhost:8025). Funciona sin claves externas. Las opcionales, de Google y Firebase, se ponen en un `.env` en la raíz (ver `.env.example`).

## Tests

```bash
cd backend
dotnet test    # los de integración necesitan Docker
```
