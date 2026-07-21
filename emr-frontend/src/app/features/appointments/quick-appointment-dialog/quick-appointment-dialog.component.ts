import { Component, inject, OnInit, signal, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, Validators } from '@angular/forms';
import { DynamicDialogRef, DynamicDialogConfig } from 'primeng/dynamicdialog';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';

import { AppointmentService } from '../../../core/services/appointment.service';
import { PatientService } from '../../../core/services/patient.service';
import { DoctorService } from '../../../core/services/doctor.service';
import { NotificationService } from '../../../core/services/notification.service';
import { debounceTime, distinctUntilChanged, switchMap, of } from 'rxjs';
import { PatientListDto } from '../../../core/models/patient.model';
import { DoctorListDto } from '../../../core/models/doctor.model';

@Component({
  selector: 'app-quick-appointment-dialog',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, ButtonModule, InputTextModule, DropdownModule
  ],
  templateUrl: './quick-appointment-dialog.component.html',
  styleUrl: './quick-appointment-dialog.component.scss'
})
export class QuickAppointmentDialogComponent implements OnInit {
  private fb = inject(FormBuilder);
  private appointmentService = inject(AppointmentService);
  private patientService = inject(PatientService);
  private doctorService = inject(DoctorService);
  private notify = inject(NotificationService);
  dialogRef = inject(DynamicDialogRef);
  config = inject(DynamicDialogConfig);
  data = this.config.data;   // { date: string, appointment?: any }

  doctors = signal<DoctorListDto[]>([]);
  doctorOptions = computed(() => this.doctors().map(d => ({ label: `Dr. ${d.fullName} — ${d.specialization || 'General Physician'}`, value: d.doctorId })));
  patientResults = signal<PatientListDto[]>([]);
  saving = signal(false);

  // Time Widget Signals
  startHour = signal('09');
  startMinute = signal('00');
  startAmPm = signal<'AM' | 'PM'>('AM');

  endHour = signal('09');
  endMinute = signal('30');
  endAmPm = signal<'AM' | 'PM'>('AM');

  form = this.fb.group({
    patientId: [null as number | null, Validators.required],
    patientSearch: [''],
    doctorId: [null as number | null, Validators.required],
    startTime: ['09:00', Validators.required],
    endTime: ['09:30', Validators.required],
    reason: ['']
  });

  ngOnInit(): void {
    this.doctorService.getActiveDoctors().subscribe(res => this.doctors.set(res));

    this.form.get('patientSearch')?.valueChanges.pipe(
      debounceTime(300),
      distinctUntilChanged(),
      switchMap(term => {
        if (!term || term.length < 2) return of({ items: [], totalCount: 0, pageNumber: 1, pageSize: 10, totalPages: 0 });
        return this.patientService.getAll({ searchTerm: term, pageNumber: 1, pageSize: 10 });
      })
    ).subscribe(res => this.patientResults.set(res.items));

    this.syncTimesToForm();
  }

  // Custom Time Widget Helper Methods
  incrementHour(type: 'start' | 'end'): void {
    if (type === 'start') {
      let h = parseInt(this.startHour(), 10);
      h = h === 12 ? 1 : h + 1;
      this.startHour.set(String(h).padStart(2, '0'));
    } else {
      let h = parseInt(this.endHour(), 10);
      h = h === 12 ? 1 : h + 1;
      this.endHour.set(String(h).padStart(2, '0'));
    }
    this.syncTimesToForm();
  }

  decrementHour(type: 'start' | 'end'): void {
    if (type === 'start') {
      let h = parseInt(this.startHour(), 10);
      h = h === 1 ? 12 : h - 1;
      this.startHour.set(String(h).padStart(2, '0'));
    } else {
      let h = parseInt(this.endHour(), 10);
      h = h === 1 ? 12 : h - 1;
      this.endHour.set(String(h).padStart(2, '0'));
    }
    this.syncTimesToForm();
  }

  incrementMinute(type: 'start' | 'end'): void {
    if (type === 'start') {
      let m = parseInt(this.startMinute(), 10);
      m = (m + 5) % 60;
      this.startMinute.set(String(m).padStart(2, '0'));
    } else {
      let m = parseInt(this.endMinute(), 10);
      m = (m + 5) % 60;
      this.endMinute.set(String(m).padStart(2, '0'));
    }
    this.syncTimesToForm();
  }

  decrementMinute(type: 'start' | 'end'): void {
    if (type === 'start') {
      let m = parseInt(this.startMinute(), 10);
      m = (m - 5 + 60) % 60;
      this.startMinute.set(String(m).padStart(2, '0'));
    } else {
      let m = parseInt(this.endMinute(), 10);
      m = (m - 5 + 60) % 60;
      this.endMinute.set(String(m).padStart(2, '0'));
    }
    this.syncTimesToForm();
  }

  toggleAmPm(type: 'start' | 'end'): void {
    if (type === 'start') {
      this.startAmPm.set(this.startAmPm() === 'AM' ? 'PM' : 'AM');
    } else {
      this.endAmPm.set(this.endAmPm() === 'AM' ? 'PM' : 'AM');
    }
    this.syncTimesToForm();
  }

  onTimeInputChange(type: 'start' | 'end'): void {
    this.syncTimesToForm();
  }

  syncTimesToForm(): void {
    const s24 = this.to24Hour(this.startHour(), this.startMinute(), this.startAmPm());
    const e24 = this.to24Hour(this.endHour(), this.endMinute(), this.endAmPm());
    this.form.patchValue({
      startTime: s24,
      endTime: e24
    });
  }

  to24Hour(hourStr: string, minStr: string, amPm: 'AM' | 'PM'): string {
    let h = parseInt(hourStr || '12', 10);
    if (isNaN(h) || h < 1) h = 12;
    if (h > 12) h = 12;
    let m = parseInt(minStr || '00', 10);
    if (isNaN(m) || m < 0) m = 0;
    if (m > 59) m = 59;

    if (amPm === 'AM' && h === 12) h = 0;
    else if (amPm === 'PM' && h < 12) h += 12;

    return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
  }

  onSelectPatient(patient: PatientListDto): void {
    this.form.patchValue({ patientId: patient.patientId, patientSearch: patient.fullName });
    this.patientResults.set([]);
  }

  displayPatientName = (patient: PatientListDto): string => patient?.fullName || '';

  onSubmit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const val = this.form.getRawValue();
    const startStr = val.startTime!.length === 5 ? val.startTime + ':00' : val.startTime!;
    const endStr = val.endTime!.length === 5 ? val.endTime + ':00' : val.endTime!;
    this.saving.set(true);

    this.appointmentService.create({
      patientId: val.patientId!,
      doctorId: val.doctorId!,
      appointmentDate: this.data.date,
      startTime: startStr,
      endTime: endStr,
      reason: val.reason || undefined
    }).subscribe({
      next: () => {
        this.notify.success('Appointment created successfully');
        this.dialogRef.close('saved');
      },
      error: (err) => {
        this.saving.set(false);
        this.notify.error(err?.error?.message || 'Failed to create appointment');
      }
    });
  }

  onCancel(): void {
    this.dialogRef.close();
  }
}