설치해야될 모델은 이거 REMEAD 에 어떻게 사용하면 좋을지 기록

- https://docs.comfy.org/ko/tutorials/flux/flux-2-klein 참고하여 Flux.2 Klein 4B 모델(4B 정제) 다운로드 및 세팅 (기본 모델)
- ComfyUI-Inspyrenet-Rembg 다운로드 필요

# Audio.json

- https://huggingface.co/Comfy-Org/stable-audio-3/tree/main 모델, 텍스트 인코더 다운로드 필요

## 변수

- #3
  text
- #4
  text
- #5
  steps
  cfg
- #9
  seconds
- #11
  value

# GenerateImage.json

원하는 모델, LoRA, Checkpoint 다운로드해서 사용

## 변수

- #1
  ckpt_name
- #2
  text
- #3
  text
- #5
  steps
  cfg
  sampler_name
- #6
  width
  height
- #9
  clip_name
  type
- #10
  vae_name
- #21
  value
- #24
  lora_name
- #25
  unet_name
- #27
  value
- #29
  value

# StyleChange.json

- #4 (긍정 프롬프트)
  text
- #38 (부정 프롬프트)
  text
- #16 (Ori Image)
  image
- #18 (Ref Image)
  image
- #8
  steps
  cfg
  denoise

# UI.json

- #8 (긍정 프롬프트)
  text
- #7 (부정 프롬프트)
  text
- #22
  value
- #17
  image
- #16
  width
  height
- #5
  steps
  cfg

# GenrateImage Only Flux.json

- #2
  text
- #3
  text
- #5
  steps
  cfg
  sampler_name
- #6
  width
  height
- #9
  clip_name
  type
- #10
  vae_name
