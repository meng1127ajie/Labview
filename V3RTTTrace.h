#ifndef INC_V3_RTT_TRACE_H_
#define INC_V3_RTT_TRACE_H_

#include <stdint.h>

#ifdef __cplusplus

class CFOC;

/* Channel 0 JustFloat: V3_RTT_FLOAT_COUNT little-endian floats + 00 00 80 7F. */
#define V3_RTT_TRACE_ENABLE       1U
#define V3_RTT_TRACE_DIVIDER      100U
#define V3_RTT_UP_CHANNEL         0U

typedef enum
{
	V3_RTT_SEQ = 0,          /* gaps in this counter mean dropped RTT frames */
	V3_RTT_TIME_MS,          /* SysTick time, ms */
	V3_RTT_RUN_STATE,        /* MtrRunSttsMchn_t */
	V3_RTT_CLB_STATUS,       /* ClbData[1]: standby/busy/success/fail */
	V3_RTT_CLB_STEP,         /* ClbData[4]: active calibration main/sub-step */
	V3_RTT_CLB_ERROR,        /* ClbData[2] */
	V3_RTT_ENCODER_RAW,      /* raw position sensor count */
	V3_RTT_SPEED_RPM,        /* mechanical speed */
	V3_RTT_VBUS_V,           /* DC bus voltage */
	V3_RTT_ID_A,             /* measured d-axis current */
	V3_RTT_IQ_A,             /* measured q-axis current */
	V3_RTT_VD_MOD,           /* d-axis modulation command, PU */
	V3_RTT_VQ_MOD,           /* q-axis modulation command, PU */
	V3_RTT_ENC_OFFSET,       /* electrical offset, 4096 = 360 deg */
	V3_RTT_LD_UH,            /* latest identified Ld, microhenry */
	V3_RTT_LQ_UH,            /* latest identified Lq, microhenry */
	V3_RTT_PSI_MWB,          /* latest identified PM flux, mWb */
	V3_RTT_RS_MOHM,          /* latest identified phase resistance, milliohm */
	V3_RTT_HF_CONTROL_HZ,    /* ClbData[6]: measured FOC call rate during HF test */
	V3_RTT_HF_INJECT_HZ,     /* ClbData[11]: measured injection frequency */
	V3_RTT_HF_CURRENT_AMP_A, /* ClbData[8]: demodulated measured-current amplitude */
	V3_RTT_HF_ENCODER_MOVE,  /* ClbData[10]: maximum encoder movement, counts */
	V3_RTT_HF_VOLTAGE_AMP_V, /* ClbData[7]: demodulated voltage amplitude */
	/* 以下字段为独立Ld/Lq扫频模式；前23个字段索引保持兼容。 */
	V3_RTT_SWP_STATE,
	V3_RTT_SWP_AXIS,
	V3_RTT_SWP_POINT_IDX,
	V3_RTT_SWP_RESULT_SEQ,
	V3_RTT_SWP_ERROR_FLAGS,
	V3_RTT_SWP_PHASE_WRAP_CNT,
	V3_RTT_SWP_SAMPLE_CNT,
	V3_RTT_SWP_FREQ_CMD_HZ,
	V3_RTT_SWP_FREQ_ACT_HZ,
	V3_RTT_SWP_CONTROL_HZ,
	V3_RTT_SWP_SIN_REF,
	V3_RTT_SWP_COS_REF,
	V3_RTT_SWP_ID_REF_PU,
	V3_RTT_SWP_IQ_REF_PU,
	V3_RTT_SWP_VD_V,
	V3_RTT_SWP_VQ_V,
	V3_RTT_SWP_V_SIN,
	V3_RTT_SWP_V_COS,
	V3_RTT_SWP_I_SIN,
	V3_RTT_SWP_I_COS,
	V3_RTT_SWP_I_AMP_A,
	V3_RTT_SWP_V_AMP_V,
	V3_RTT_SWP_PHASE_DEG,
	V3_RTT_SWP_R_OHM,
	V3_RTT_SWP_X_OHM,
	V3_RTT_SWP_L_LINE_UH,
	V3_RTT_SWP_L_PHASE_UH,
	V3_RTT_SWP_I_TONE_PURITY,
	V3_RTT_SWP_V_TONE_PURITY,
	V3_RTT_SWP_ENCODER_MOVE,
	V3_RTT_SWP_SPEED_MAX_RPM,
	V3_RTT_SWP_MOD_MAX,
	V3_RTT_SWP_VBUS_MIN_V,
	V3_RTT_FLOAT_COUNT
} V3RTTField_t;

void V3RTT_Init(void);
void V3RTT_Trace(const CFOC *pFOC, uint8_t runState);

#endif
#endif
