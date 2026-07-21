import { Component, OnInit, inject, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';

import { FullCalendarModule, FullCalendarComponent } from '@fullcalendar/angular';
import { CalendarOptions, DateSelectArg, EventClickArg, EventDropArg } from '@fullcalendar/core';
import dayGridPlugin from '@fullcalendar/daygrid';
import timeGridPlugin from '@fullcalendar/timegrid';
import interactionPlugin from '@fullcalendar/interaction';

import { FormsModule } from '@angular/forms';

import { DialogService, DynamicDialogRef } from 'primeng/dynamicdialog';
import { DropdownModule } from 'primeng/dropdown';
import { ButtonModule } from 'primeng/button';

import { AppointmentService } from '../../../core/services/appointment.service';
import { DoctorService } from '../../../core/services/doctor.service';
import { NotificationService } from '../../../core/services/notification.service';
import { QuickAppointmentDialogComponent } from '../quick-appointment-dialog/quick-appointment-dialog.component';
import { DoctorListDto } from '../../../core/models/doctor.model';

@Component({
  selector: 'app-appointment-calendar',
  standalone: true,
  imports: [
    CommonModule, FullCalendarModule, FormsModule, DropdownModule, ButtonModule
  ],
  providers: [DialogService],
  templateUrl: './appointment-calendar.component.html',
  styleUrl: './appointment-calendar.component.scss'
})
export class AppointmentCalendarComponent implements OnInit {
  @ViewChild('calendar') calendarComponent!: FullCalendarComponent;

  private appointmentService = inject(AppointmentService);
  private doctorService = inject(DoctorService);
  private notify = inject(NotificationService);
  private dialogService = inject(DialogService);
  private ref: DynamicDialogRef | undefined;
  private router = inject(Router);

  doctors: DoctorListDto[] = [];
  doctorOptions: any[] = [];
  doctorFilter: number | null = null;

  calendarOptions: CalendarOptions = {
    plugins: [dayGridPlugin, timeGridPlugin, interactionPlugin],
    initialView: 'dayGridMonth',
    headerToolbar: {
      left: 'prev,next today',
      center: 'title',
      right: 'dayGridMonth,timeGridWeek,timeGridDay'
    },
    editable: true,          // Drag-drop enable
    selectable: true,        // Date click/select enable
    selectMirror: true,
    dayMaxEvents: true,
    events: [],
    select: (arg) => this.onDateSelect(arg),
    eventClick: (arg) => this.onEventClick(arg),
    eventDrop: (arg) => this.onEventDrop(arg),
    eventResize: (arg) => this.onEventDrop(arg as any),
    datesSet: (arg) => this.loadEvents(arg.startStr, arg.endStr),
    height: 'auto'
  };

  ngOnInit(): void {
    this.doctorService.getActiveDoctors().subscribe(res => {
      this.doctors = res;
      this.doctorOptions = [
        { label: 'All Doctors', value: null },
        ...res.map(d => ({ label: `Dr. ${d.fullName}`, value: d.doctorId }))
      ];
    });
  }

  onList(): void {
    this.router.navigate(['/appointments']);
  }

  onAdd(): void {
    this.router.navigate(['/appointments/add']);
  }

  onDoctorFilterChange(): void {
    const api = this.calendarComponent.getApi();
    this.loadEvents(
      api.view.activeStart.toISOString(),
      api.view.activeEnd.toISOString()
    );
  }

  loadEvents(from: string, to: string): void {
    this.appointmentService.getCalendarEvents(from, to, this.doctorFilter ?? undefined).subscribe({
      next: (events) => {
        const api = this.calendarComponent?.getApi();
        if (!api) return;
        api.removeAllEvents();
        events.forEach(e => {
          api.addEvent({
            id: e.appointmentId.toString(),
            title: e.title,
            start: e.start,
            end: e.end,
            backgroundColor: e.color,
            borderColor: e.color
          });
        });
      },
      error: () => this.notify.error('Failed to load calendar events')
    });
  }

  onDateSelect(arg: DateSelectArg): void {
    const clickedDate = arg.startStr.split('T')[0];
    this.openQuickDialog(clickedDate);
    arg.view.calendar.unselect();
  }

  private openQuickDialog(dateStr: string, existingAppointment?: any) {
    this.ref = this.dialogService.open(QuickAppointmentDialogComponent, {
      header: existingAppointment ? 'Edit Appointment' : 'New Appointment',
      width: '500px',
      data: {
        date: dateStr,
        appointment: existingAppointment
      },
      contentStyle: { overflow: 'auto' },
      baseZIndex: 10000
    });

    this.ref.onClose.subscribe(result => {
      if (result === 'saved') {
        const api = this.calendarComponent.getApi();
        this.loadEvents(api.view.activeStart.toISOString(), api.view.activeEnd.toISOString());
      }
    });
  }

  onEventClick(arg: EventClickArg): void {
    const appointmentId = arg.event.id;
    this.router.navigate(['/appointments/edit', appointmentId]);
  }

  onEventDrop(arg: EventDropArg): void {
    const appointmentId = +arg.event.id;
    const newStart: Date = arg.event.start!;
    const newEnd: Date = arg.event.end ?? new Date(newStart.getTime() + 30 * 60000);

    const newDate = this.formatDate(newStart);
    const newStartTime = this.formatTime(newStart);
    const newEndTime = this.formatTime(newEnd);

    this.appointmentService.reschedule(appointmentId, {
      appointmentId,
      newDate,
      newStartTime,
      newEndTime
    }).subscribe({
      next: () => this.notify.success('Appointment rescheduled successfully'),
      error: (err) => {
        this.notify.error(err?.error?.message || 'Failed to reschedule');
        arg.revert();   // ⚠️ Important: error aaye to calendar pe wapas purani jagah le jao
      }
    });
  }

  private formatDate(date: Date): string {
    const y = date.getFullYear();
    const m = String(date.getMonth() + 1).padStart(2, '0');
    const d = String(date.getDate()).padStart(2, '0');
    return `${y}-${m}-${d}`;
  }

  private formatTime(date: Date): string {
    const h = String(date.getHours()).padStart(2, '0');
    const m = String(date.getMinutes()).padStart(2, '0');
    return `${h}:${m}:00`;
  }
}