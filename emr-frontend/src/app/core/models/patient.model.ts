export interface PatientListDto {
  patientId: number;
  fullName: string;
  age: number;
  gender: string;
  mobile: string;
  photoUrl?: string;
  isActive: boolean;
}

export interface PatientDetailDto {
  patientId: number;
  fullName: string;
  age: number;
  gender: string;
  bloodGroup?: string;
  mobile: string;
  email?: string;
  address?: string;
  photoUrl?: string;
  isActive: boolean;
  createdAt: string;
}

export interface PatientCreateDto {
  fullName: string;
  age: number;
  gender: string;
  bloodGroup?: string;
  mobile: string;
  email?: string;
  address?: string;
}

export interface PatientUpdateDto extends PatientCreateDto {
  patientId: number;
}

export interface PatientQueryParams {
  searchTerm?: string;
  gender?: string;
  pageNumber: number;
  pageSize: number;
  sortBy?: string;
  sortDescending?: boolean;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  pageNumber: number;
  pageSize: number;
  totalPages: number;
}

export interface ExtractedMedicationItem {
  id?: number;
  medicineName: string;
  dosage: string;
  frequency: string;
  duration?: string;
}

export interface ExtractedLabItem {
  testName: string;
  observedValue: string;
  referenceRange: string;
  unit?: string;
  status?: string;
  category?: string;
  isAbnormal: boolean;
}

export interface ExtractedMedicalDataDto {
  documentId: number;
  category: string;
  documentTitle: string;
  extractedDate: string;
  fileUrl?: string;
  doctorName?: string;
  hospitalName?: string;
  diagnoses?: string[];
  medications?: ExtractedMedicationItem[];
  labFindings?: ExtractedLabItem[];
  radiologyImpression?: string;
  clinicalSummary?: string;
  rawTextSummary?: string;
  rawOcrText?: string;
}

export interface PatientDocumentRecord {
  id: number;
  category: string;
  fileName: string;
  fileUrl?: string;
  filePath?: string;
  fileSize?: string;
  uploadedAt: string;
  summary?: string;
  extractedData?: ExtractedMedicalDataDto;
}
