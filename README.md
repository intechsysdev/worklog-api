# work_log_api

API de **WorkLog · Bitácora de actividades**, app de la plataforma INTECHSYS ONE.

Stack: .NET 10, minimal APIs, EF Core 10 sobre SQL Server y arquitectura hexagonal.

Los usuarios, la sesión y las empresas son de **ONE**. WorkLog guarda solo lo propio de cada persona dentro de la bitácora: rol, sede, grupo, supervisor y dispositivo.

## Arquitectura

```
src/
  WorkLog.Domain          Núcleo puro, sin paquetes: entidades con comportamiento, objetos de valor y reglas.
                          Jornada (agregado), Geofence, ShiftResolver, WorkdayPolicy, WorkdayTimeline,
                          EffectiveParameters, ScheduleTemplate (validación legal)…
  WorkLog.Application     Casos de uso (puertos de entrada: I…Service) y puertos de salida
                          (repositorios, IUnitOfWork, IIdentityProvider, IFileStorage, IAuditTrail).
                          Solo depende del dominio.
  WorkLog.Infrastructure  Adaptadores de salida: EF Core/SQL Server, cliente de ONE (con resiliencia),
                          almacenamiento de archivos, seed (festivos de Colombia, parámetros legales).
  WorkLog.Api             Adaptador de entrada: endpoints, autenticación contra ONE, resolución de
                          empresa (X-Tenant-Id) y permisos por endpoint. Raíz de composición.
tests/
  WorkLog.UnitTests          Reglas del dominio (horarios, jornada, geocercas, parámetros, festivos).
  WorkLog.ArchitectureTests  Las dependencias apuntan al dominio (NetArchTest).
```

Las pruebas de arquitectura verifican estas reglas:
- El dominio no conoce EF, ASP.NET Core ni otras capas.
- La aplicación no conoce adaptadores.
- Las entidades no tienen setters públicos.
- Los casos de uso son internos y se exponen solo por interfaz.

### Decisiones

| Tema | Decisión |
|---|---|
| Identidad | Cada token se valida preguntando a ONE (`/auth/me`), con caché de 15 s por hash del token. WorkLog nunca tiene la llave de firma de ONE. |
| Empresa | La cabecera `X-Tenant-Id` se valida contra `/me/apps/worklog/tenants`. Un filtro global de EF aísla los datos por empresa. |
| Primer ingreso | Owner/Admin de la empresa en ONE entra como Administrador y se crean los roles, categorías y resultados de la empresa. Los demás necesitan invitación: se ligan por correo al primer ingreso. |
| Permisos | Son 12 permisos por rol (matriz del diseño) y se verifican con `.RequirePermission(...)` en cada endpoint. |
| Sin conexión | El teléfono genera los ids (jornada y actividad) y un `clientEventId` por evento. El servidor procesa cada evento una sola vez (`ProcessedClientEvents`), así que reintentar es seguro. `/me/workday/sync` procesa la cola en orden. |
| Concurrencia | La jornada tiene `Version` como token de concurrencia, un índice único (persona, día) y una sola actividad abierta a la vez. |
| Hora | La hora del evento es la del teléfono, acotada a no más de 2 min en el futuro y no más de 7 días en el pasado. Un inicio encolado antes de la ventana del turno se corrige al inicio de la ventana (`Adjusted`). |
| Auditoría | Solo se agregan registros, en la misma transacción que el cambio. Un trigger SQL impide `UPDATE`/`DELETE`. |
| Parámetros | La cadena es Global → País → Empresa → Sede, con fecha de vigencia. Los legales solo se editan por país y exigen una norma que los soporte. |
| MobiControl | Fase 2: un puerto con adaptador simulado; el adaptador real sale de Geomonitoreo. |

## Correr en local

Requisitos: SDK .NET 10 (`global.json`) y SQL Server. La cadena de desarrollo apunta a `JEFO-PC` con seguridad integrada.

```bash
dotnet run --project src/WorkLog.Api --launch-profile http    # http://localhost:5290  ·  /scalar/v1  ·  /health
dotnet test                                                   # unitarias + arquitectura
```

Al arrancar se aplican las migraciones y los datos del producto: la política de datos v1.0, los festivos de Colombia (año anterior a +2) y los parámetros legales de Colombia.

### Modo desarrollo (sin ONE)

`appsettings.Development.json` trae `Auth:Mode = Development`. En este modo el token es el literal `dev:<correo>`:

```bash
curl -H "Authorization: Bearer dev:admin@intechsys.dev" http://localhost:5290/api/v1/me
```

- `admin@intechsys.dev` entra como Owner de la empresa de prueba, es decir, como Administrador.
- Cualquier otro correo necesita ser invitado antes (`POST /api/v1/members`).
- El arranque falla si este modo se activa fuera del entorno Development.

### Modo ONE

Pon `Auth:Mode = One` y `One:BaseUrl` (p. ej. `http://localhost:5180/`). WorkLog tiene que estar registrado en one-api; la propuesta está en `docs/one-api/WorkLogContrato.cs`:
- slug `worklog`
- redirect URIs del panel y de la app
- origen del panel en `Cors:AllowedOrigins` de ONE
- app asignada a cada empresa

## Migraciones

```bash
dotnet tool restore
dotnet ef migrations add <NombreEnEspañol> --project src/WorkLog.Infrastructure --startup-project src/WorkLog.Infrastructure --output-dir Persistence/Migrations
```

La fábrica de diseño usa `WORKLOG_DESIGN_CONNECTION`; si no está definida, usa la base local.

## Configuración y secretos

Como en los demás repositorios de Intechsys, la configuración va en los appsettings: `appsettings.Development.json` para local y `appsettings.Production.json` para producción (conexión a Azure SQL, ONE y CORS). Este último viaja al repositorio porque el despliegue por GitHub lo necesita.

## Superficie HTTP (Fase 1)

El contrato completo está en `docs/openapi.v1.json` y en `/scalar/v1`.

| Grupo | Rutas |
|---|---|
| Mi cuenta | `GET /me`, `GET /me/tenants`, `GET/POST /me/privacy-policy[/accept]`, `GET /time` |
| Mi jornada (app) | `GET /me/today`, `GET /me/timeline`, `GET /me/workday/activity-options`, `POST /me/workday/{start,close,activities,…/pause,…/resume,…/finish,…/evidences,locations,sync}` |
| Equipo | `GET /team/today` |
| Organización | `/branches`, `/groups`, `/sites` |
| Horarios | `/schedules/templates[/preview]`, `/schedules/assignments`, `/schedules/calendar/{memberId}` |
| Actividades | `/catalog`, `/catalog/{categories,activities,outcomes}` |
| OT | `/work-orders` (CRUD, cancelar, importar CSV) |
| Usuarios | `/members`, `/roles`, `/roles/permissions` |
| Parámetros | `/parameters/{level}[/{code}]?scope=` |

## Fase 2

| Grupo | Rutas |
|---|---|
| Mis solicitudes (app) | `GET /me/week`, `GET /me/requests`, `POST /me/requests/{adjustment,overtime,shift-change}`, `POST /me/requests/{id}/cancel` |
| Aprobaciones | `GET /approvals[/pending-count,/{id}]`, `POST /approvals/{id}/{approve,reject}` |
| Novedades | `GET/POST /absences`, `POST /absences/preview`, `POST/GET /absences/{id}/support`, `POST /absences/{id}/{approve,reject}` |
| Horas | `GET /hours/team` |
| Cierre de periodo | `GET /payroll/{branchId}/periods[/{from}]`, `POST …/{close,reopen}`, `GET …/export?format=Xlsx\|Csv` |
| Auditoría | `GET /audit[/{id},/entity-types,/export]`, `GET /members/{id}/locations` (queda auditado) |
| Dispositivos | `GET/PUT /device-control/settings`, `POST /device-control/verify`, `GET /device-control/{devices,apps}`, `PUT /members/{id}/device` |

Reglas de la Fase 2:
- **Motor de horas** (`HoursCalculator`). Clasifica cada minuto pagado, que es la jornada sin las pausas:
  - Es ordinario hasta lo programado del día y hasta el tope semanal (lunes a domingo); lo demás es extra.
  - Es nocturno de 19:00 a 06:00, según el parámetro del país.
  - Domingo y festivo se cuentan aparte, porque son un recargo.
- **Solicitudes.** Al aprobar se aplica el efecto:
  - Ajuste: la jornada se corrige sin poder desordenarse.
  - Horas extra: se valida el máximo diario y semanal.
  - Cambio de turno: crea cambios puntuales.

  Además:
  - Nadie aprueba sus propias solicitudes.
  - El rechazo exige comentario.
  - Un periodo cerrado bloquea los ajustes de sus fechas.
- **Cierre de periodo.** Quincenas por sede.
  - Se cierra solo cuando el periodo ya terminó y con el checklist completo.
  - Al cerrar, las horas quedan congeladas en `PayrollLines`.
  - Solo el administrador reabre.
  - La exportación (xlsx con ClosedXML o csv) exige el periodo cerrado.
- **Cierre automático.** `WorkdayAutomationJob` corre cada 5 min (`Automation:IntervalMinutes`) y recorre cada empresa con su contexto.
  - Cierra al fin del turno más la gracia, o al fin de las horas extra aprobadas si es más tarde.
  - La jornada queda `PendingReview`.
- **MobiControl.** Puerto `IDeviceManagementGateway` con un simulador (flota estable por empresa, `MobiControl:FailingChecks` para probar fallos).
  - Al abrir o cerrar la jornada se envía `WL_JORNADA` y, si aplica, se cierra la sesión de Shared Device.
  - Con política `Block`, si MobiControl no responde no se deja iniciar.
  - El adaptador REST real se toma de geomonitoreo-api.
- **Archivos.** `Storage:Provider = Local | AzureBlob`. Para Azure se configuran `Storage:ConnectionString` como secreto y `Storage:Container`.

## Próximas fases

- Invitación completa contra ONE: crear o ligar al usuario en ONE desde WorkLog.
- Adaptador REST real de MobiControl.
- Push (FCM) y avisos del servidor (no inicio de jornada al usuario y al supervisor).
- Catálogo de países y ciudades (GeoNames) y carga de festivos por país.
- Paquete legal por país con validación del asesor laboral.
- Registro de WorkLog en one-api (propuesta en `docs/one-api/WorkLogContrato.cs`).
