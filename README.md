# Recepción · Hospital CRM

CRM de atención hospitalaria con bandeja WhatsApp, agente con herramientas, contactos, empresas, seguimientos, historial y agenda. Inspirado en el enfoque de [Comp AI CRM](https://github.com/trycompai/crm); implementación propia en Next.js/shadcn, .NET 10 y PostgreSQL.

**Estado:** aplicación funcional y probada localmente en contenedores. El uso con pacientes reales requiere conectar Keycloak/Hospital, Google OAuth y los números de negocio, y completar la validación operativa indicada en [despliegue](docs/deployment.md). Las credenciales de prueba no equivalen a una puesta en producción.

## Iniciar localmente

Requisitos: Docker Compose; Python 3 para preparar el entorno. Node 22 LTS sólo si desarrollas o ejecutas las pruebas de navegador fuera del contenedor.

```sh
python3 scripts/init-dev.py
docker compose up -d --build
```

Abre [Recepción local](http://localhost:3215). En modo demo: usuario `admin`, contraseña `demo-recepcion`. También existen `agent`, `doctor` y `other` (otra empresa). La base contiene exclusivamente ejemplos sintéticos. `init-dev.py` conserva cualquier `.env` existente y genera secretos aleatorios en una instalación nueva.

La API escucha en `127.0.0.1:5215`, el frontend en `127.0.0.1:3215`; PostgreSQL sólo está disponible en la red de contenedores. `SEND_ENABLED=false`, canales pausados y agente desactivado por defecto impiden envíos accidentales. Ninguna clave va en variables `NEXT_PUBLIC_*`.

## Funciones

- Usuarios compartidos con Hospital mediante Keycloak, membresía única y cinco roles. Aislamiento de empresa en API, persistencia, canales, herramientas del agente y auditoría.
- Contactos con búsqueda/paginación, empresas y convenios, oportunidades por etapas, actividades y notas persistentes.
- Bandeja con conversaciones, asignación, pausa/reanudación, historial, estados de entrega, archivos y audios. Transcripción si Kapso la proporciona; en su ausencia, revisión humana.
- Alta de clientes Kapso por hospital, enlaces de conexión y múltiples números generales o de doctores. La coexistencia se verifica en Kapso; ecos desde Business App pausan al bot.
- Agente WhatsApp con NVIDIA NIM configurable: guía del negocio, horarios, consulta de citas propias, propuestas de agenda con confirmación, recetas firmadas y transferencia al equipo. No crea ni modifica prescripciones.
- Asistente interno para métricas anónimas, notas y seguimientos del contacto seleccionado. La selección se resuelve en servidor; no se cargan fichas ni historiales al prompt interno.
- Agenda de Hospital como fuente; consultar, crear, reprogramar y cancelar mediante su API. Google Calendar recibe un espejo sin nombres ni teléfonos de pacientes.
- Cola persistente, deduplicación de webhooks, firma HMAC, auditoría y tratamiento explícito de entregas inciertas.

## Arquitectura

```mermaid
flowchart LR
  Staff[Recepción / doctor] --> Web[Next.js + shadcn]
  Web -->|sesión HttpOnly / BFF| API[.NET 10 API + workers]
  Keycloak[Keycloak de Hospital] --> Web
  Keycloak --> API
  Kapso[Kapso / WhatsApp] -->|webhook firmado| API
  API --> Kapso
  API --> PG[(PostgreSQL)]
  API --> NIM[NVIDIA NIM]
  API --> Hospital[API Hospital]
  API --> Google[Google Calendar]
```

El navegador no recibe tokens de proveedor. El tenant procede del JWT firmado; en webhooks se resuelve por el número registrado. El teléfono y el vínculo al paciente se comprueban contra Hospital antes de cada consulta privada. Los PDFs pasan en memoria, sin enlaces públicos permanentes.

## Verificación

```sh
python3 tests/api_smoke.py
scripts/test-backend.sh
npm ci --prefix frontend
cd frontend
npx playwright install chromium
npm test
```

Las pruebas API y navegador requieren el modo demo local; no se ejecutan contra producción. Backend usa una base temporal de PostgreSQL que se elimina al terminar y proveedores simulados. Las pruebas reales opcionales `scripts/check-integrations.py`, `scripts/kapso-mcp-read.py`, `scripts/check-nim.py` y `tests/assistant_smoke.py` requieren las claves locales; las pruebas NIM usan texto sintético y consumen cuota.

[Resultados y límites de validación](docs/verification.md) · [Backlog](docs/backlog.md) · [Easypanel y operación](docs/deployment.md) · [Contrato Hospital](docs/hospital-integration.md) · [Puente de recetas](integrations/hospital/README.md)
