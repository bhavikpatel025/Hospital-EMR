import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';
import { firstValueFrom, Observable } from 'rxjs';
import { ExtractedMedicalDataDto } from '../models/patient.model';

@Injectable({
  providedIn: 'root'
})
export class VoiceService {
  private http = inject(HttpClient);
  
  // State signals
  public isRecording = signal(false);
  public isTranscribing = signal(false);
  
  private mediaRecorder: MediaRecorder | null = null;
  private audioChunks: Blob[] = [];

  constructor() {}

  async startRecording(): Promise<void> {
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
      this.mediaRecorder = new MediaRecorder(stream);
      this.audioChunks = [];

      this.mediaRecorder.ondataavailable = (event) => {
        if (event.data.size > 0) {
          this.audioChunks.push(event.data);
        }
      };

      this.mediaRecorder.start();
      this.isRecording.set(true);
    } catch (error) {
      console.error('Error accessing microphone:', error);
      throw new Error('Microphone access denied or unavailable.');
    }
  }

  async stopRecording(): Promise<string> {
    return new Promise((resolve, reject) => {
      if (!this.mediaRecorder) {
        reject('No active recording found.');
        return;
      }

      this.mediaRecorder.onstop = async () => {
        this.isRecording.set(false);
        this.isTranscribing.set(true);

        const audioBlob = new Blob(this.audioChunks, { type: 'audio/webm' });
        
        // Stop all tracks to release the microphone
        this.mediaRecorder?.stream.getTracks().forEach(track => track.stop());

        try {
          const text = await this.uploadToGroq(audioBlob);
          resolve(text);
        } catch (error) {
          reject(error);
        } finally {
          this.isTranscribing.set(false);
        }
      };

      this.mediaRecorder.stop();
    });
  }

  private async uploadToGroq(audioBlob: Blob): Promise<string> {
    const formData = new FormData();
    // Groq expects the file with extension, we use .webm since it's the standard browser output
    formData.append('audio', audioBlob, 'dictation.webm');

    const response = await firstValueFrom(
      this.http.post<{text: string}>(`${environment.apiUrl}/Voice/transcribe`, formData)
    );
    
    
    return response.text;
  }

  extractVoiceData(text: string): Observable<ExtractedMedicalDataDto> {
    return this.http.post<ExtractedMedicalDataDto>(`${environment.apiUrl}/Voice/extract`, { text });
  }
}
