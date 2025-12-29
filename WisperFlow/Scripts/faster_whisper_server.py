#!/usr/bin/env python3
"""
Faster-Whisper Server - A high-performance transcription server.
Communicates via JSON over stdin/stdout for integration with C# app.

Protocol:
- Input: {"command": "transcribe", "audio_path": "...", "language": "en"}
- Output: {"success": true, "text": "...", "duration": 1.23}
- Or: {"success": false, "error": "..."}

Commands:
- transcribe: Transcribe an audio file
- ping: Health check
- quit: Shutdown server
"""

import sys
import json
import time
import os

# Suppress warnings before importing faster_whisper
os.environ["KMP_DUPLICATE_LIB_OK"] = "TRUE"
import warnings
warnings.filterwarnings("ignore")

try:
    from faster_whisper import WhisperModel
except ImportError:
    print(json.dumps({"success": False, "error": "faster-whisper not installed. Run: pip install faster-whisper"}), flush=True)
    sys.exit(1)


class FasterWhisperServer:
    def __init__(self):
        self.model = None
        self.model_size = None
        
    def load_model(self, model_size: str = "base", device: str = "auto", compute_type: str = "auto"):
        """Load the Whisper model."""
        try:
            # Determine best compute type based on device
            if compute_type == "auto":
                if device == "cuda":
                    compute_type = "float16"  # Fast on GPU
                else:
                    compute_type = "int8"  # Fast on CPU
            
            if device == "auto":
                # Try CUDA first, fall back to CPU
                try:
                    import torch
                    device = "cuda" if torch.cuda.is_available() else "cpu"
                except ImportError:
                    device = "cpu"
            
            self.model = WhisperModel(
                model_size,
                device=device,
                compute_type=compute_type,
                download_root=os.path.join(os.environ.get("LOCALAPPDATA", "."), "WisperFlow", "models", "faster-whisper")
            )
            self.model_size = model_size
            return {"success": True, "model": model_size, "device": device, "compute_type": compute_type}
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def transcribe(self, audio_path: str, language: str = None) -> dict:
        """Transcribe an audio file."""
        if self.model is None:
            return {"success": False, "error": "Model not loaded"}
        
        if not os.path.exists(audio_path):
            return {"success": False, "error": f"Audio file not found: {audio_path}"}
        
        try:
            start_time = time.time()
            
            # Load audio using numpy/wave for better compatibility with NAudio WAV files
            audio_data = self._load_audio(audio_path)
            
            # Transcribe with faster-whisper
            segments, info = self.model.transcribe(
                audio_data,
                language=language,
                beam_size=1,  # Faster with greedy decoding
                best_of=1,
                temperature=0,
                condition_on_previous_text=False,  # Faster
                vad_filter=True,  # Skip silence
                vad_parameters=dict(
                    min_silence_duration_ms=500,
                    speech_pad_ms=200
                )
            )
            
            # Collect all text
            text_parts = []
            for segment in segments:
                text_parts.append(segment.text)
            
            full_text = "".join(text_parts).strip()
            duration = time.time() - start_time
            
            return {
                "success": True,
                "text": full_text,
                "duration": round(duration, 3),
                "language": info.language,
                "language_probability": round(info.language_probability, 3)
            }
        except Exception as e:
            return {"success": False, "error": str(e)}
    
    def _load_audio(self, audio_path: str):
        """Load audio file and convert to numpy array for faster-whisper."""
        import wave
        import numpy as np
        
        try:
            # Try loading with wave module first (works well with NAudio WAV files)
            with wave.open(audio_path, 'rb') as wf:
                sample_rate = wf.getframerate()
                n_channels = wf.getnchannels()
                sample_width = wf.getsampwidth()
                n_frames = wf.getnframes()
                
                # Read raw audio data
                raw_data = wf.readframes(n_frames)
                
                # Convert to numpy array
                if sample_width == 2:  # 16-bit
                    audio = np.frombuffer(raw_data, dtype=np.int16).astype(np.float32) / 32768.0
                elif sample_width == 4:  # 32-bit
                    audio = np.frombuffer(raw_data, dtype=np.int32).astype(np.float32) / 2147483648.0
                else:
                    raise ValueError(f"Unsupported sample width: {sample_width}")
                
                # Convert stereo to mono if needed
                if n_channels > 1:
                    audio = audio.reshape(-1, n_channels).mean(axis=1)
                
                # Resample to 16kHz if needed (faster-whisper expects 16kHz)
                if sample_rate != 16000:
                    # Simple resampling (not ideal but works)
                    ratio = 16000 / sample_rate
                    new_length = int(len(audio) * ratio)
                    indices = np.linspace(0, len(audio) - 1, new_length)
                    audio = np.interp(indices, np.arange(len(audio)), audio)
                
                return audio.astype(np.float32)
                
        except Exception as e:
            # Fall back to file path (let faster-whisper handle it)
            return audio_path
    
    def run(self):
        """Main server loop - read commands from stdin, write responses to stdout."""
        # Signal ready
        print(json.dumps({"ready": True, "version": "1.0"}), flush=True)
        
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            
            try:
                request = json.loads(line)
            except json.JSONDecodeError as e:
                print(json.dumps({"success": False, "error": f"Invalid JSON: {e}"}), flush=True)
                continue
            
            command = request.get("command", "")
            
            if command == "ping":
                print(json.dumps({"success": True, "pong": True, "model": self.model_size}), flush=True)
            
            elif command == "load":
                model_size = request.get("model_size", "base")
                device = request.get("device", "auto")
                compute_type = request.get("compute_type", "auto")
                result = self.load_model(model_size, device, compute_type)
                print(json.dumps(result), flush=True)
            
            elif command == "transcribe":
                audio_path = request.get("audio_path", "")
                language = request.get("language")
                result = self.transcribe(audio_path, language)
                print(json.dumps(result), flush=True)
            
            elif command == "quit":
                print(json.dumps({"success": True, "goodbye": True}), flush=True)
                break
            
            else:
                print(json.dumps({"success": False, "error": f"Unknown command: {command}"}), flush=True)


if __name__ == "__main__":
    server = FasterWhisperServer()
    server.run()

