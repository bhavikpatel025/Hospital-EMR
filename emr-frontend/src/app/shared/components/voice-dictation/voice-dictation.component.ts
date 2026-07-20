import { Component, EventEmitter, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { VoiceService } from '../../../core/services/voice.service';
import { MessageService } from 'primeng/api';

@Component({
  selector: 'app-voice-dictation',
  standalone: true,
  imports: [CommonModule, ButtonModule],
  templateUrl: './voice-dictation.component.html',
  styleUrl: './voice-dictation.component.scss'
})
export class VoiceDictationComponent {
  public voiceService = inject(VoiceService);
  private messageService = inject(MessageService);

  @Output() textTranscribed = new EventEmitter<string>();

  async toggleRecording() {
    if (this.voiceService.isRecording()) {
      try {
        const text = await this.voiceService.stopRecording();
        if (text && text.trim().length > 0) {
          this.textTranscribed.emit(text);
          this.messageService.add({ severity: 'success', summary: 'Dictation Saved', detail: 'Audio transcribed successfully by Groq AI.' });
        } else {
          this.messageService.add({ severity: 'warn', summary: 'No Audio', detail: 'No speech was detected.' });
        }
      } catch (error) {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to transcribe audio.' });
      }
    } else {
      try {
        await this.voiceService.startRecording();
      } catch (error) {
        this.messageService.add({ severity: 'error', summary: 'Microphone Error', detail: 'Could not access the microphone.' });
      }
    }
  }
}
