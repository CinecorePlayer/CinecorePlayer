# Sostituto del ciclo madVR64.ax+0x114f90..0x114fbe (0.92.17): impacchetta la crominanza UV 16 bit in RGBA16
# (per pixel: dword UV della sorgente, poi dword r12d). Stessi byte, stessi r8/r9/r10 all'uscita.
# Usa solo eax, ecx, edx, xmm0-2 (volatili e non usati dalla funzione). Termina con un salto assoluto a 0x114fbe.
    .text
    .globl cave
cave:
    movl (%rsi), %eax
    cltd
    subl %edx, %eax
    sarl %eax               # limite = larghezza/2 (come madVR)
    movl %eax, %ecx
    subl %r10d, %ecx        # giri da fare
    cmpl $1, %ecx
    jge 1f
    movl $1, %ecx           # madVR fa sempre almeno un giro (do-while)
1:  movl %ecx, %edx
    shrl $2, %edx
    movd %r12d, %xmm2
    pshufd $0, %xmm2, %xmm2
    testl %edx, %edx
    jz 3f
2:  movdqu (%r9), %xmm0
    movdqa %xmm0, %xmm1
    punpckldq %xmm2, %xmm0
    punpckhdq %xmm2, %xmm1
    movdqu %xmm0, (%r8)
    movdqu %xmm1, 16(%r8)
    addq $16, %r9
    addq $32, %r8
    decl %edx
    jnz 2b
3:  addl %ecx, %r10d
    andl $3, %ecx
    jz 5f
4:  movl (%r9), %eax
    movl %eax, (%r8)
    movl %r12d, 4(%r8)
    addq $4, %r9
    addq $8, %r8
    decl %ecx
    jnz 4b
5:  jmp *0(%rip)
    .quad 0x1122334455667788
