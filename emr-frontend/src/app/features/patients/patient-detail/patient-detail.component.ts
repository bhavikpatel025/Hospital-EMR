import { CommonModule } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog, MatDialogModule } from '@angular/material/dialog';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { AppointmentListDto } from '../../../core/models/appointment.model';
import {
  PatientDetailDto, ExtractedMedicalDataDto, ExtractedMedicationItem,
  ExtractedLabItem, PatientDocumentRecord
} from '../../../core/models/patient.model';
import { AppointmentService } from '../../../core/services/appointment.service';
import { PatientService } from '../../../core/services/patient.service';
import { NotificationService } from '../../../core/services/notification.service';
import { ConfirmDialogComponent } from '../../../shared/components/confirm-dialog/confirm-dialog.component';
import { environment } from '../../../../environments/environment';

// PrimeNG v19/v21 Standalone Component Imports
import { CardModule } from 'primeng/card';
import { TableModule } from 'primeng/table';
import { TabsModule } from 'primeng/tabs';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { SelectButtonModule } from 'primeng/selectbutton';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { VoiceDictationComponent } from '../../../shared/components/voice-dictation/voice-dictation.component';

@Component({
  selector: 'app-patient-detail',
  standalone: true,
  imports: [
    CommonModule, RouterLink, MatIconModule, MatDialogModule, FormsModule,
    CardModule, TableModule, TabsModule, DialogModule, ButtonModule,
    TagModule, ProgressSpinnerModule, SelectButtonModule, InputTextModule,
    TextareaModule, TooltipModule, VoiceDictationComponent
  ],
  templateUrl: './patient-detail.component.html',
  styleUrl: './patient-detail.component.scss'
})
export class PatientDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly patientService = inject(PatientService);
  private readonly appointmentService = inject(AppointmentService);
  private readonly notify = inject(NotificationService);
  private readonly sanitizer = inject(DomSanitizer);
  private readonly dialog = inject(MatDialog);

  protected readonly patient = signal<PatientDetailDto | null>(null);
  protected readonly appointments = signal<AppointmentListDto[]>([]);
  protected readonly loading = signal(true);

  // Smart Document OCR & Auto-Entry state
  protected readonly activeTab = signal<string | number>('records');
  
  // Voice Dictation state
  protected readonly dictatedNote = signal<string>('');

  protected readonly selectedDocCategory = signal<'Prescription' | 'LabReport' | 'Radiology'>('Prescription');
  protected readonly docCategories = [
    { label: 'Prescription', value: 'Prescription', icon: 'pi pi-file-edit' },
    { label: 'Lab Report', value: 'LabReport', icon: 'pi pi-chart-bar' },
    { label: 'Radiology / Scan', value: 'Radiology', icon: 'pi pi-camera' }
  ];
  protected readonly isExtracting = signal(false);
  protected readonly useAiExtraction = signal(true);
  protected readonly extractedData = signal<ExtractedMedicalDataDto | null>(null);
  protected readonly showVerificationModal = signal(false);

  // Document Preview Modal state
  protected readonly selectedPreviewDoc = signal<PatientDocumentRecord | null>(null);
  protected readonly showPreviewModal = signal(false);

  // Real Database-driven Clinical Tables
  protected readonly uploadedDocuments = signal<PatientDocumentRecord[]>([]);
  protected readonly patientMedications = signal<ExtractedMedicationItem[]>([]);
  protected readonly patientLabs = signal<ExtractedLabItem[]>([]);
  protected readonly patientImaging = signal<any[]>([]);
  protected readonly clinicalSummary = signal<any | null>(null);

  protected readonly initials = computed(() => {
    const name = this.patient()?.fullName ?? '';
    return name
      .split(' ')
      .filter(Boolean)
      .slice(0, 2)
      .map(part => part.charAt(0).toUpperCase())
      .join('');
  });

  formatBullets(text?: string): string[] {
    if (!text || !text.trim()) return [];

    const cleaned = text.trim();

    // 1. If it already explicitly uses bullet symbols (`•`, `-`, `*`), split exactly by those items
    if (cleaned.includes('•') || cleaned.includes('\n- ') || cleaned.includes('\n* ')) {
      return cleaned
        .split(/(?:\r?\n|•|\n- |\n\* )/)
        .map(line => line.replace(/^[\s\-*•]+/, '').trim())
        .filter(line => line.length > 3);
    }

    // 2. If it has newlines, split by newlines
    if (cleaned.includes('\n')) {
      const lines = cleaned
        .split(/\r?\n/)
        .map(l => l.trim())
        .filter(l => l.length > 3);
      if (lines.length > 1) return lines;
    }

    // 3. Split paragraph sentences by period or semicolon
    let sentenceSplit = cleaned
      .split(/(?<=[a-zA-Z]{3,}|\]|\))[.;]\s+(?=[A-Z0-9])/i)
      .map(s => s.trim().replace(/[.]+$/, ''))
      .filter(s => s.length > 5);

    // 4. If any sentence itself is long and has comma-separated items or 'including' (e.g. 6 medications list)
    const refinedBullets: string[] = [];
    for (const item of (sentenceSplit.length > 0 ? sentenceSplit : [cleaned])) {
      if (item.length > 120 && (item.includes(', including ') || item.includes(': ') || (item.match(/,/g) || []).length >= 3)) {
        let parts: string[] = [];
        if (item.includes(', including ')) {
          const splitInc = item.split(', including ');
          refinedBullets.push(splitInc[0] + ' (Key Regimen/List):');
          parts = splitInc[1].split(/,\s*(?:and\s+)?/i);
        } else if (item.includes(': ')) {
          const splitCol = item.split(': ');
          refinedBullets.push(splitCol[0] + ':');
          parts = splitCol[1].split(/,\s*(?:and\s+)?/i);
        } else {
          parts = item.split(/,\s*(?:and\s+)?/i);
        }
        for (const p of parts) {
          const trimmedP = p.trim().replace(/[.]+$/, '');
          if (trimmedP.length > 4) {
            refinedBullets.push(trimmedP);
          }
        }
      } else {
        refinedBullets.push(item);
      }
    }

    let result = refinedBullets.length > 0 ? refinedBullets : [cleaned];

    // Adaptive Goldilocks rule: If normal, keep 3 to 4. If complex/many, group and cap neatly between 5 to 7 points
    // so it never looks like an overly long, cluttered list on the screen!
    while (result.length > 7) {
      let minLen = 999999;
      let minIdx = result.length > 1 ? 1 : 0;
      for (let i = (result.length > 2 ? 1 : 0); i < result.length - 1; i++) {
        const combinedLen = result[i].length + result[i + 1].length;
        if (combinedLen < minLen) {
          minLen = combinedLen;
          minIdx = i;
        }
      }
      result[minIdx] = `${result[minIdx]}; ${result[minIdx + 1]}`;
      result.splice(minIdx + 1, 1);
    }

    return result;
  }

  ngOnInit(): void {
    const patientId = Number(this.route.snapshot.paramMap.get('id'));
    if (!patientId) {
      this.router.navigate(['/patients']);
      return;
    }

    this.patientService.getById(patientId).subscribe({
      next: patient => {
        this.patient.set(patient);
        this.loadPatientRecords(patientId);

        this.appointmentService.getAll({
          pageNumber: 1,
          pageSize: 6,
          searchTerm: patient.fullName,
          sortBy: 'AppointmentDate',
          sortDescending: true
        })
          .pipe(catchError(() => of({ items: [], totalCount: 0, pageNumber: 1, pageSize: 6, totalPages: 0 })))
          .subscribe(result => {
            this.appointments.set(result.items.filter(appointment => appointment.patientId === patientId));
            this.loading.set(false);
          });
      },
      error: () => {
        this.notify.error('Unable to load patient details.');
        this.router.navigate(['/patients']);
      }
    });
  }

  private loadPatientRecords(patientId: number): void {
    this.patientService.getPatientRecords(patientId)
      .pipe(catchError(() => of({ documents: [], medications: [], labFindings: [], radiologyNotes: [] })))
      .subscribe(res => {
        if (res.documents) {
          this.uploadedDocuments.set(res.documents.map((d: any) => ({
            id: d.id,
            category: d.category,
            fileName: d.fileName,
            fileUrl: d.fileUrl ? this.buildBackendFileUrl(d.fileUrl) : undefined,
            filePath: d.filePath,
            fileSize: '1.2 MB',
            uploadedAt: new Date(d.uploadedAt).toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' }),
            summary: d.rawTextSummary,
            extractedData: {} as any
          })));
        }
        if (res.medications) {
          this.patientMedications.set(res.medications.map((m: any) => ({
            id: m.id,
            medicineName: m.medicineName,
            dosage: m.dosage,
            frequency: m.frequency,
            duration: m.duration
          })));
        }
        if (res.labFindings) {
          this.patientLabs.set(res.labFindings.map((l: any) => ({
            testName: l.testName,
            observedValue: l.observedValue,
            referenceRange: l.referenceRange,
            unit: l.unit,
            status: l.status,
            category: l.category,
            isAbnormal: l.isAbnormal
          })));
        }
        if (res.radiologyNotes) {
          this.patientImaging.set(res.radiologyNotes.map((r: any) => ({
            id: r.id,
            impressionText: r.impressionText
          })));
        }
      });

    this.patientService.getPatientClinicalSummary(patientId)
      .pipe(catchError(() => of(null)))
      .subscribe(summary => {
        if (summary) {
          this.clinicalSummary.set(summary);
        }
      });
  }

  protected onTabChange(val: string | number | undefined): void {
    if (val !== undefined) {
      this.activeTab.set(val);
    }
  }

  protected selectTab(tab: string | number): void {
    this.activeTab.set(tab);
  }

  protected setDocCategory(cat: 'Prescription' | 'LabReport' | 'Radiology'): void {
    this.selectedDocCategory.set(cat);
  }

  protected onFileSelected(event: any): void {
    const file: File = event.target.files?.[0];
    if (!file) return;

    // Validate file type
    const allowedTypes = ['application/pdf', 'image/jpeg', 'image/jpg', 'image/png'];
    const allowedExtensions = ['.pdf', '.jpg', '.jpeg', '.png'];
    const fileExtension = '.' + file.name.split('.').pop()?.toLowerCase();

    if (!allowedExtensions.includes(fileExtension) && !allowedTypes.includes(file.type)) {
      this.notify.error('Invalid file type. Please upload PDF, JPG, or PNG files only.');
      event.target.value = '';
      return;
    }

    // Validate file size (25 MB max)
    if (file.size > 25 * 1024 * 1024) {
      this.notify.error('File too large. Maximum allowed size is 25 MB.');
      event.target.value = '';
      return;
    }

    const patientId = this.patient()?.patientId ?? 1;
    this.isExtracting.set(true);

    const isAi = this.useAiExtraction();
    this.notify.info(isAi ? `Analyzing ${file.name} with AI Structured Extraction...` : `Scanning ${file.name} with Tesseract OCR...`);

    const upload$ = isAi
      ? this.patientService.uploadAndExtractWithAI(patientId, this.selectedDocCategory(), file)
      : this.patientService.uploadAndExtractDocument(patientId, this.selectedDocCategory(), file);

    upload$
      .pipe(
        catchError((err) => {
          console.error('Document Extraction Error:', err);
          this.notify.error('Backend extraction failed. Please ensure API server (`dotnet run`) is running.');
          return of(null);
        })
      )
      .subscribe(data => {
        this.isExtracting.set(false);
        if (data) {
          // Build full fileUrl from backend response
          if (data.fileUrl) {
            data.fileUrl = this.buildBackendFileUrl(data.fileUrl);
          }

          this.extractedData.set(data);
          this.showVerificationModal.set(true);

          // Show appropriate notification based on extraction results
          const hasMeds = (data.medications?.length ?? 0) > 0;
          const hasLabs = (data.labFindings?.length ?? 0) > 0;
          const hasRadiology = !!data.radiologyImpression;

          if (hasMeds || hasLabs || hasRadiology) {
            this.notify.success('OCR extracted data from your document. Please review and confirm.');
          } else {
            this.notify.info('No structured data could be auto-extracted. You can enter data manually in the verification modal.');
          }

          // Reload document list since file was persisted to DB
          this.loadPatientRecords(patientId);
        }
        event.target.value = '';
      });
  }

  protected closeVerificationModal(): void {
    this.showVerificationModal.set(false);
    this.extractedData.set(null);
  }

  protected confirmAutoEntry(): void {
    const data = this.extractedData();
    const patientId = this.patient()?.patientId ?? 1;
    if (!data) return;

    const payload = {
      medications: data.medications || [],
      labFindings: data.labFindings || [],
      radiologyImpression: data.radiologyImpression || ''
    };

    // Check if there's anything to save
    const hasData = payload.medications.length > 0 || payload.labFindings.length > 0 || payload.radiologyImpression.trim().length > 0;
    if (!hasData) {
      this.notify.info('No records to save. Add entries manually or close the modal.');
      return;
    }

    this.notify.info('Saving extracted records to database...');

    this.patientService.saveExtractedRecords(patientId, payload)
      .pipe(catchError(() => {
        this.notify.error('Could not connect to database endpoint.');
        return of(null);
      }))
      .subscribe(res => {
        if (res) {
          this.notify.success('✔ Saved! Extracted medical records permanently entered into EMR Database.');
          // Re-load clinical tables from real database
          this.loadPatientRecords(patientId);
          if (data.category === 'Prescription') this.activeTab.set('meds');
          else if (data.category === 'LabReport') this.activeTab.set('labs');
          else if (data.category === 'Radiology') this.activeTab.set('imaging');
          else this.activeTab.set('records');
        }
        this.closeVerificationModal();
      });
  }

  // ==================================================================================
  // FILE TYPE HELPERS — used by HTML template for preview logic
  // ==================================================================================

  protected isImageFile(nameOrUrl: string | undefined | null): boolean {
    if (!nameOrUrl) return false;
    const lower = nameOrUrl.toLowerCase();
    return lower.endsWith('.jpg') || lower.endsWith('.jpeg') || lower.endsWith('.png')
      || lower.endsWith('.gif') || lower.endsWith('.bmp') || lower.endsWith('.tiff');
  }

  protected isPdfFile(nameOrUrl: string | undefined | null): boolean {
    if (!nameOrUrl) return false;
    return nameOrUrl.toLowerCase().endsWith('.pdf');
  }

  protected getSafeUrl(url: string | undefined | null): SafeResourceUrl {
    if (!url) return '';
    return this.sanitizer.bypassSecurityTrustResourceUrl(url);
  }

  // ==================================================================================
  // DOCUMENT PREVIEW MODAL — used by archive tab to view uploaded documents
  // ==================================================================================

  protected openPreviewModal(doc: PatientDocumentRecord): void {
    this.selectedPreviewDoc.set(doc);
    this.showPreviewModal.set(true);
  }

  protected closePreviewModal(): void {
    this.selectedPreviewDoc.set(null);
    this.showPreviewModal.set(false);
  }

  // ==================================================================================
  // VERIFICATION MODAL — Add Row helpers for manual entry
  // ==================================================================================

  protected addMedicationRow(): void {
    const data = this.extractedData();
    if (!data) return;

    if (!data.medications) {
      data.medications = [];
    }
    data.medications.push({
      medicineName: '',
      dosage: '',
      frequency: '',
      duration: ''
    });
    // Trigger signal update
    this.extractedData.set({ ...data });
  }

  protected addLabFindingRow(): void {
    const data = this.extractedData();
    if (!data) return;

    if (!data.labFindings) {
      data.labFindings = [];
    }
    data.labFindings.push({
      testName: '',
      observedValue: '',
      referenceRange: '',
      unit: '',
      status: 'Normal',
      category: 'General',
      isAbnormal: false
    });
    // Trigger signal update
    this.extractedData.set({ ...data });
  }

  // ==================================================================================
  // NAVIGATION & UTILITY
  // ==================================================================================

  protected backToList(): void {
    this.router.navigate(['/patients']);
  }

  protected statusClass(isActive: boolean): string {
    return isActive ? 'emr-pill--success' : 'emr-pill--danger';
  }

  protected get nextAppointment(): AppointmentListDto | null {
    return this.appointments().find(appointment => appointment.status !== 'Cancelled') ?? null;
  }

  /**
   * Build a full backend URL from a relative path returned by the API.
   * Backend returns paths like "/uploads/medical_records/patient_1/file.jpg"
   * We prepend the API base URL (e.g. "https://localhost:7218") to make it accessible.
   */
  private buildBackendFileUrl(relativePath: string): string {
    if (!relativePath) return '';
    // Extract base URL from apiUrl (remove /api suffix)
    const apiBase = environment.apiUrl.replace(/\/api\/?$/, '');
    return `${apiBase}${relativePath}`;
  }

  protected getImpressionText(imp: any): string {
    return imp?.impressionText || imp || '';
  }

  protected onDeleteDocument(doc: any): void {
    const dialogRef = this.dialog.open(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Document',
        message: `Are you sure you want to delete "${doc.fileName}"? This action cannot be undone.`,
        confirmText: 'Delete',
        cancelText: 'Cancel'
      }
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result && this.patient()?.patientId && doc.id) {
        this.patientService.deletePatientDocument(this.patient()!.patientId, doc.id).subscribe({
          next: () => {
            this.notify.success('Document deleted successfully.');
            this.loadPatientRecords(this.patient()!.patientId);
          },
          error: () => this.notify.error('Failed to delete document.')
        });
      }
    });
  }

  protected onDeleteMedication(med: any): void {
    const dialogRef = this.dialog.open(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Medication',
        message: `Are you sure you want to delete "${med.medicineName}" from current roster?`,
        confirmText: 'Delete',
        cancelText: 'Cancel'
      }
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result && this.patient()?.patientId && med.id) {
        this.patientService.deletePatientMedication(this.patient()!.patientId, med.id).subscribe({
          next: () => {
            this.notify.success('Medication record deleted.');
            this.loadPatientRecords(this.patient()!.patientId);
          },
          error: () => this.notify.error('Failed to delete medication record.')
        });
      }
    });
  }

  protected onDeleteImaging(imp: any): void {
    const dialogRef = this.dialog.open(ConfirmDialogComponent, {
      width: '450px',
      data: {
        title: 'Delete Radiology Note',
        message: 'Are you sure you want to delete this radiology/imaging note?',
        confirmText: 'Delete',
        cancelText: 'Cancel'
      }
    });

    dialogRef.afterClosed().subscribe(result => {
      if (result && this.patient()?.patientId && imp.id) {
        this.patientService.deletePatientRadiology(this.patient()!.patientId, imp.id).subscribe({
          next: () => {
            this.notify.success('Radiology note deleted.');
            this.loadPatientRecords(this.patient()!.patientId);
          },
          error: () => this.notify.error('Failed to delete radiology note.')
        });
      }
    });
  }
}
