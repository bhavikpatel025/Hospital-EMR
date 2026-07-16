# 🏥 Enterprise Hospital Electronic Medical Records (EMR) System

![Angular](https://img.shields.io/badge/Angular-18%2B-dd0031?style=for-the-badge&logo=angular&logoColor=white)
![.NET Core](https://img.shields.io/badge/.NET%208-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)
![Entity Framework Core](https://img.shields.io/badge/EF%20Core-Clean%20Architecture-239120?style=for-the-badge&logo=c-sharp&logoColor=white)
![PrimeNG](https://img.shields.io/badge/UI-PrimeNG%20%26%20Material-007AD9?style=for-the-badge&logo=primeng&logoColor=white)
![Groq AI](https://img.shields.io/badge/AI%20Engine-Groq%20Llama--3.3%20%2F%20Vision-FF6F00?style=for-the-badge&logo=ai&logoColor=white)

An advanced, highly responsive, and AI-augmented **Hospital Electronic Medical Records (EMR) System** engineered from scratch using **Clean Architecture** on the backend (.NET 8 Web API + Entity Framework Core) and reactive **Angular Signals** + **PrimeNG** on the frontend.

This enterprise portal streamlines hospital clinical workflows by combining robust daily OPD management with a state-of-the-art **Hybrid Medical Document Extraction Engine (Local OCR + Groq Multimodal Vision AI)** to instantly auto-fill patient clinical summaries, laboratory findings, and doctor prescriptions.

---

## ✨ Key Highlights & Enterprise Features

### 🔐 1. Authentication & Role-Based Security
* **JWT Bearer Token Security:** Stateless, secure token-based authentication with salted password hashing.
* **Angular Route Guards (`AuthGuard`):** Strict frontend route protection ensuring unauthorized users are automatically redirected to the login portal.
* **Global HTTP Error & Auth Interceptor (`error.interceptor.ts`):** Automatically catches expired sessions (`401 Unauthorized`), server errors (`500`), and network disconnects, alerting users via sleek PrimeNG Toast notifications (`p-toast`).

### 🧑‍⚕️ 2. Comprehensive Patient Management (`Reactive Signals`)
* **Patient Lifecycle Tracking:** Manage patient demographics, Unique Hospital ID (`UHID`), Age, Gender, Blood Group, Mobile contact, and Active status.
* **Reactive Angular Signals (`signal<T>`):** Granular UI reactivity ensuring ultra-fast table rendering without `Zone.js` overhead.
* **Debounced Instant Search & Pagination:** Live search filtering (`debounceTime(400)`) with custom server-side pagination and dynamic `"Showing X to Y of Z entries"` calculations.
* **Profile Photo Management:** Patient avatar upload and static file hosting.

### 👨‍⚕️ 3. Doctor Roster & OPD Scheduling
* **Doctor Directory:** Manage specialized doctors, qualifications, experience years, and consultation fee structures.
* **Interactive OPD Appointments:** Doctor-Patient linked appointment booking with real-time status management (`Pending`, `Confirmed`, `Completed`, `Cancelled`).
* **Live Clinical Dashboard:** Interactive summary cards and daily OPD queue overview.

---

## 🤖 4. Smart Hybrid Medical Document Processing (Local OCR + Groq AI)

The core innovation of this EMR is its dual-flow intelligent document extraction system capable of reading both digital lab reports and messy handwritten doctor prescriptions:

```
[ Upload Document (.PDF / .JPG / .PNG) ]
                   │
                   ▼
       [ Local Tesseract OCR Engine ]
                   │
         ┌─────────┴─────────┐
         ▼                   ▼
 (Text Found >= 30 chars)    (Low Text < 30 chars / Handwritten)
         │                   │
         ▼                   ▼
  [ Groq Text-AI Engine ]    [ Groq Multimodal Vision AI ]
 (`llama-3.3-70b-versatile`) (`llama-3.2-90b-vision-preview`)
         │                   │
         └─────────┬─────────┘
                   ▼
  [ Structured JSON Auto-Fill in UI ]
  (Medications, Lab Findings, Radiology)
```

### 🔥 Flow A: Local OCR + Regular Expression Fallback
* Utilizes **`UglyToad.PdfPig`** for digital PDF parsing and local **`Tesseract OCR (eng)`** for text recognition.
* Runs custom clinical regex parsers to identify common medical keywords and dosages at zero API cost.

### ⚡ Flow B: AI-Powered Structured Extraction (`Groq / Llama-3`)
* **Text AI Engine (`llama-3.3-70b-versatile` / `grok-beta`):** Prompts the LLM with strict `response_format: { type: "json_object" }` instructions to parse printed lab reports, complete blood counts (`CBC`), and discharge summaries into structured arrays (`Medications`, `LabFindings`, `RadiologyNotes`).
* **Multimodal Vision AI Fallback (`llama-3.2-90b-vision-preview`):** Automatically triggers when an uploaded image yields low/empty OCR text (`< 30 characters`), routing the Base64 image payload directly to Groq's Vision model. It reads messy cursive handwritten prescriptions (`Rx Amox 625mg TDS x 5 days`) with crystal clarity!

### 🎨 Sleek UI Integration
* **Single-Row Header Alignment:** Upload Dropzone title aligned horizontally with the `⚡ AI Extraction (Structured)` toggle switch.
* **Document Category Badges:** Quick pre-upload classification (`Prescription pi-file-edit` | `Lab Report` | `Radiology / Scan`).

---

## 🛡️ Enterprise Project Stabilization Shield

To ensure zero downtime and unhandled application crashes, the project incorporates a hardened stability shield:
1. **Global Exception Handling Middleware (`GlobalExceptionHandlingMiddleware.cs`):** Intercepts all unhandled C# server errors, logging details and returning standardized JSON responses (`{ success: false, statusCode: 500, message: "..." }`).
2. **Standardized Angular Error Interceptor:** Provides real-time user-friendly feedback for network connectivity loss (`Status 0`), unauthorized attempts (`401/403`), and bad request validation failures (`400`).

---

## 🏗️ Clean Architecture Breakdown

The `.NET 8` backend strictly adheres to **Clean Architecture / Onion Architecture** principles, enforcing separation of concerns across 5 distinct layers:

```
┌────────────────────────────────────────────────────────┐
│                   Angular 18 Frontend                  │
│       (Signals, PrimeNG, Reactive Forms, Guards)       │
└───────────────────────────┬────────────────────────────┘
                            │ REST API / JWT
┌───────────────────────────▼────────────────────────────┐
│                       EMR.API                          │
│     (Controllers, Global Middleware, Static Files)     │
└───────────────────────────┬────────────────────────────┘
                            │
┌───────────────────────────▼────────────────────────────┐
│                   EMR.Application                      │
│   (Services, DTOs, AutoMapper, Validation, Interfaces) │
└──────────────┬───────────────────────────┬─────────────┘
               │                           │
┌──────────────▼─────────────┐   ┌─────────▼─────────────┐
│         EMR.Domain         │   │  EMR.Infrastructure   │
│ (Entities, Enums, Core FKs)│   │ (EF Core, Repos, Groq)│
└────────────────────────────┘   └───────────────────────┘
```

* **`EMR.Domain`**: Core enterprise entities (`Patient`, `Doctor`, `Appointment`, `PatientDocument`, `User`, `Role`) and Enums (`AppointmentStatus`). Zero external dependencies.
* **`EMR.Application`**: Business logic, Data Transfer Objects (`PatientCreateDto`, `AiExtractedDocumentDto`, `MedicationItemDto`), AutoMapper profiles, and Service interfaces (`IAiDocumentExtractionService`).
* **`EMR.Infrastructure`**: EF Core `AppDbContext`, SQL Server/PostgreSQL database migrations, Repository implementations, and third-party integrations (`GroqAiDocumentExtractionService`).
* **`EMR.API`**: REST controllers (`PatientsController`, `AiDocumentExtractionController`, `AppointmentsController`), Swagger/OpenAPI configuration with JWT Bearer support, and CORS setup.
* **`EMR.Shared`**: Common utilities and password hashing helpers.

---

## 🛠️ Technology Stack

| Layer | Technologies & Tools |
| :--- | :--- |
| **Frontend Framework** | Angular 18+ (Standalone Components, Signals (`signal()`), Reactive Forms) |
| **UI Library & Styling** | PrimeNG (`p-toast`, `p-dropdown`), Angular Material (`MatPaginator`, `MatDialog`), SCSS |
| **Backend Framework** | .NET 8 Web API (C# 12) |
| **ORM & Database** | Entity Framework Core 8, SQL Server / PostgreSQL |
| **AI & LLM Engine** | Groq API (`llama-3.3-70b-versatile` Text Model, `llama-3.2-90b-vision-preview` Vision Model) |
| **OCR & PDF Engines** | Local Tesseract OCR (`TesseractEngine`), UglyToad.PdfPig |
| **Authentication** | JSON Web Tokens (`System.IdentityModel.Tokens.Jwt`), Custom PBKDF2 / SHA Hashing |

---

## 🚀 Getting Started & Local Setup

### 📋 Prerequisites
* [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
* [Node.js (v18+ or v20+)](https://nodejs.org/) & [Angular CLI](https://angular.dev/tools/cli) (`npm install -g @angular/cli`)
* SQL Server or PostgreSQL instance running locally.

### ⚙️ 1. Backend Setup (`EMR.Backend`)
1. Navigate to the backend root directory:
   ```bash
   cd EMR.Backend
   ```
2. Restore project dependencies and build all architectural layers:
   ```bash
   dotnet restore
   dotnet build
   ```
3. Configure your Database Connection and Groq API Key inside `EMR.API/appsettings.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Server=localhost;Database=HospitalEMR_DB;Trusted_Connection=True;TrustServerCertificate=True;"
     },
     "Groq": {
       "ApiKey": "gsk_YOUR_GROQ_API_KEY_HERE",
       "Model": "llama-3.3-70b-versatile"
     }
   }
   ```
4. Apply Entity Framework Core migrations and start the server:
   ```bash
   dotnet run --project EMR.API/EMR.API.csproj
   ```
   *The API will start at `http://localhost:5000` / `https://localhost:5001`. Swagger UI is accessible at `/swagger`.*

### 💻 2. Frontend Setup (`emr-frontend`)
1. Open a new terminal window and navigate to the Angular workspace:
   ```bash
   cd emr-frontend
   ```
2. Install Node packages and dependencies:
   ```bash
   npm install
   ```
3. Launch the local development server:
   ```bash
   ng serve
   ```
4. Open your browser and navigate to `http://localhost:4200`.

---

## 🧪 Testing the AI Document Extraction Flow
1. Log into the EMR portal and open any **Patient Detail** page (`/patients/view/{id}`).
2. Ensure the `⚡ AI Extraction (Structured)` toggle switch is set to **ON**.
3. **Test Digital Reports:** Upload any computer-typed PDF (e.g., Blood Sugar / CBC test). The system extracts all table parameters and auto-fills the `Clinical Summary`, `Medications`, and `Lab Findings` instantly.
4. **Test Handwritten Prescriptions:** Upload a messy doctor's prescription (`.jpg/.png`). The Smart Multimodal Vision AI detects low OCR output and directly deciphers cursive handwriting to auto-populate the medication roster!

---

## 📄 License & Contributing
This project is developed as an Enterprise-grade EMR demonstration and production architecture template. Contributions, bug reports, and pull requests are welcome!

Made with ❤️ using **Clean Architecture**, **Angular Signals**, and **Multimodal Vision AI**.
