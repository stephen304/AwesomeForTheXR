; Unity 4.5.5p5 x86. Windows has just produced a LocalFree-owned argv block.
; Both existing callers keep ownership and their original EBP frame.
.386
.model flat
option casemap:none
EXTERN _NormalizeAwesomeArgs:PROC
EXTERN _launchText:BYTE
EXTERN _NormalizeAwesomeEngineArgs:PROC
.code
PUBLIC _StartupTable
_StartupTable LABEL BYTE
    DD IMAGEREL engineArgs
    DD IMAGEREL monoArgs
    DD IMAGEREL graphicsArgs

normalize MACRO argcOffset, returnRva
    LOCAL here
    mov ecx, [esp]
    sub ecx, returnRva         ; actual ASLR-adjusted base of Awesome.exe
    push eax                   ; save Unity's argv
    call here
here:
    pop edx
    sub edx, IMAGEREL here
    add edx, IMAGEREL _launchText
    push edx
    push ecx
    lea ecx, [ebp + argcOffset]
    push ecx
    push eax
    call _NormalizeAwesomeArgs
    add esp, 16
    pop eax
ENDM

engineArgs:
    normalize -80h, 1f368ah
    mov [ebp-44h], eax         ; displaced instructions, including condition flags
    cmp eax, ebx
    ret
monoArgs:
    normalize -4, 22412h
    mov [ebp-0ch], eax
    mov eax, [ebp-4]
    ret

; Unity registers a separate CRT narrow vector for renderer/monitor lookup.
; Replace only the arguments passed to that registration, then tail-call it.
graphicsArgs:
    mov eax, [esp]
    sub eax, 1f36edh
    lea edx, [eax+1fd970h]
    push edx                  ; return into the original registration function
    call graphicsHere
graphicsHere:
    pop edx
    sub edx, IMAGEREL graphicsHere
    add edx, IMAGEREL _launchText
    push edx
    push eax
    push DWORD PTR [ebp-80h]  ; normalized wide argc, argv from this caller
    push DWORD PTR [ebp-44h]
    lea ecx, [esp+28]         ; address of registration's argv argument
    push ecx
    lea ecx, [esp+28]         ; address of registration's argc argument
    push ecx
    call _NormalizeAwesomeEngineArgs
    add esp, 24
    ret
END
