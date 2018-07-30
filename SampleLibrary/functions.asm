_DATA SEGMENT

_DATA ENDS

_TEXT SEGMENT

; ******************************************************************************************

; This function does a jump, where the distance depends on a given value x. No memory accesses (like jump table lookups) are performed.
; RCX: 0 <= x < 5
different_branch_target PROC
	lea rax, [jumptargets]
	add rax, rcx
	jmp rax

jumptargets:
	nop
	nop
	nop
	nop
	nop

	ret
different_branch_target ENDP

; ******************************************************************************************

; This function returns one of RDX and R8, depending on the last bit of the value x in RCX.
; RCX: 0 <= x < 5
; RDX: Address 1
; R8: Address 2
select_address PROC
	; This code must have the same branching structure for each input, so create mask to XOR on address 2
	; rcx[0] == 0 -> rcx = 11...111
	; rcx[0] == 1 -> rcx = 00...000
	and rcx, 1
	dec rcx
	xor rdx, r8
	and rdx, rcx

	; Add mask to address 2 again => if rcx[0] == 0, then we get address 1, else address 2
	mov rax, r8
	xor rax, rdx

	ret
select_address ENDP

; ******************************************************************************************

_TEXT ENDS
END