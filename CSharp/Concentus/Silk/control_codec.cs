﻿using Concentus.Common.CPlusPlus;
using Concentus.Common;
using Concentus.Silk.Enums;
using Concentus.Silk.Structs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Concentus.Silk
{
    public static class control_codec
    {
        /// <summary>
        /// Control encoder
        /// </summary>
        /// <param name="psEnc">I/O  Pointer to Silk encoder state</param>
        /// <param name="encControl">I    Control structure</param>
        /// <param name="TargetRate_bps">I    Target max bitrate (bps)</param>
        /// <param name="allow_bw_switch">I    Flag to allow switching audio bandwidth</param>
        /// <param name="channelNb">I    Channel number</param>
        /// <param name="force_fs_kHz"></param>
        /// <returns></returns>
        public static int silk_control_encoder(
            silk_encoder_state_fix psEnc,
            silk_EncControlStruct encControl,
            int TargetRate_bps,
            int allow_bw_switch,
            int channelNb,
            int force_fs_kHz)
        {
            int fs_kHz;
            int ret = SilkError.SILK_NO_ERROR;

            psEnc.sCmn.useDTX = encControl.useDTX;
            psEnc.sCmn.useCBR = encControl.useCBR;
            psEnc.sCmn.API_fs_Hz = encControl.API_sampleRate;
            psEnc.sCmn.maxInternal_fs_Hz = encControl.maxInternalSampleRate;
            psEnc.sCmn.minInternal_fs_Hz = encControl.minInternalSampleRate;
            psEnc.sCmn.desiredInternal_fs_Hz = encControl.desiredInternalSampleRate;
            psEnc.sCmn.useInBandFEC = encControl.useInBandFEC;
            psEnc.sCmn.nChannelsAPI = encControl.nChannelsAPI;
            psEnc.sCmn.nChannelsInternal = encControl.nChannelsInternal;
            psEnc.sCmn.allow_bandwidth_switch = allow_bw_switch;
            psEnc.sCmn.channelNb = channelNb;

            if (psEnc.sCmn.controlled_since_last_payload != 0 && psEnc.sCmn.prefillFlag == 0)
            {
                if (psEnc.sCmn.API_fs_Hz != psEnc.sCmn.prev_API_fs_Hz && psEnc.sCmn.fs_kHz > 0)
                {
                    /* Change in API sampling rate in the middle of encoding a packet */
                    ret = silk_setup_resamplers(psEnc, psEnc.sCmn.fs_kHz);
                }
                return ret;
            }

            /* Beyond this point we know that there are no previously coded frames in the payload buffer */

            /********************************************/
            /* Determine internal sampling rate         */
            /********************************************/
            fs_kHz = control_audio_bandwidth.silk_control_audio_bandwidth(psEnc.sCmn, encControl);
            if (force_fs_kHz != 0)
            {
                fs_kHz = force_fs_kHz;
            }
            /********************************************/
            /* Prepare resampler and buffered data      */
            /********************************************/
            ret = silk_setup_resamplers(psEnc, fs_kHz);

            /********************************************/
            /* Set internal sampling frequency          */
            /********************************************/
            ret = silk_setup_fs(psEnc, fs_kHz, encControl.payloadSize_ms);

            /********************************************/
            /* Set encoding complexity                  */
            /********************************************/
            ret = silk_setup_complexity(psEnc.sCmn, encControl.complexity);

            /********************************************/
            /* Set packet loss rate measured by farend  */
            /********************************************/
            psEnc.sCmn.PacketLoss_perc = encControl.packetLossPercentage;

            /********************************************/
            /* Set LBRR usage                           */
            /********************************************/
            ret = silk_setup_LBRR(psEnc.sCmn, TargetRate_bps);

            psEnc.sCmn.controlled_since_last_payload = 1;

            return ret;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="psEnc">I/O</param>
        /// <param name="fs_kHz">I</param>
        /// <returns></returns>
        public static int silk_setup_resamplers(silk_encoder_state_fix psEnc, int fs_kHz)
        {
            int ret = 0;

            if (psEnc.sCmn.fs_kHz != fs_kHz || psEnc.sCmn.prev_API_fs_Hz != psEnc.sCmn.API_fs_Hz)
            {
                if (psEnc.sCmn.fs_kHz == 0)
                {
                    /* Initialize the resampler for enc_API.c preparing resampling from API_fs_Hz to fs_kHz */
                    ret += Resampler.silk_resampler_init(psEnc.sCmn.resampler_state, psEnc.sCmn.API_fs_Hz, fs_kHz * 1000, 1);
                }
                else
                {
                    Pointer<short> x_buf_API_fs_Hz;
                    silk_resampler_state_struct temp_resampler_state = null;

                    Pointer<short> x_bufFIX = psEnc.x_buf;

                    int api_buf_samples;
                    int old_buf_samples;
                    int buf_length_ms;

                    buf_length_ms = Inlines.silk_LSHIFT(psEnc.sCmn.nb_subfr * 5, 1) + SilkConstants.LA_SHAPE_MS;
                    old_buf_samples = buf_length_ms * psEnc.sCmn.fs_kHz;
                    
                    /* Initialize resampler for temporary resampling of x_buf data to API_fs_Hz */
                    temp_resampler_state = new silk_resampler_state_struct();
                    ret += Resampler.silk_resampler_init(temp_resampler_state, Inlines.silk_SMULBB(psEnc.sCmn.fs_kHz, 1000), psEnc.sCmn.API_fs_Hz, 0);

                    /* Calculate number of samples to temporarily upsample */
                    api_buf_samples = buf_length_ms * Inlines.silk_DIV32_16(psEnc.sCmn.API_fs_Hz, 1000);

                    /* Temporary resampling of x_buf data to API_fs_Hz */
                    x_buf_API_fs_Hz = Pointer.Malloc<short>(api_buf_samples);
                    ret += Resampler.silk_resampler(temp_resampler_state, x_buf_API_fs_Hz, x_bufFIX, old_buf_samples);

                    /* Initialize the resampler for enc_API.c preparing resampling from API_fs_Hz to fs_kHz */
                    ret += Resampler.silk_resampler_init(psEnc.sCmn.resampler_state, psEnc.sCmn.API_fs_Hz, Inlines.silk_SMULBB(fs_kHz, 1000), 1);

                    /* Correct resampler state by resampling buffered data from API_fs_Hz to fs_kHz */
                    ret += Resampler.silk_resampler(psEnc.sCmn.resampler_state, x_bufFIX, x_buf_API_fs_Hz, api_buf_samples);
                }
            }

            psEnc.sCmn.prev_API_fs_Hz = psEnc.sCmn.API_fs_Hz;

            return ret;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="psEnc">I/O</param>
        /// <param name="fs_kHz">I</param>
        /// <param name="PacketSize_ms">I</param>
        /// <returns></returns>
        public static int silk_setup_fs(
            silk_encoder_state_fix psEnc,
            int fs_kHz,
            int PacketSize_ms)
        {
            int ret = SilkError.SILK_NO_ERROR;

            /* Set packet size */
            if (PacketSize_ms != psEnc.sCmn.PacketSize_ms)
            {
                if ((PacketSize_ms != 10) &&
                    (PacketSize_ms != 20) &&
                    (PacketSize_ms != 40) &&
                    (PacketSize_ms != 60))
                {
                    ret = SilkError.SILK_ENC_PACKET_SIZE_NOT_SUPPORTED;
                }
                if (PacketSize_ms <= 10)
                {
                    psEnc.sCmn.nFramesPerPacket = 1;
                    psEnc.sCmn.nb_subfr = PacketSize_ms == 10 ? 2 : 1;
                    psEnc.sCmn.frame_length = Inlines.silk_SMULBB(PacketSize_ms, fs_kHz);
                    psEnc.sCmn.pitch_LPC_win_length = Inlines.silk_SMULBB(SilkConstants.FIND_PITCH_LPC_WIN_MS_2_SF, fs_kHz);
                    if (psEnc.sCmn.fs_kHz == 8)
                    {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_10_ms_NB_iCDF.GetPointer();
                    }
                    else {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_10_ms_iCDF.GetPointer();
                    }
                }
                else {
                    psEnc.sCmn.nFramesPerPacket = Inlines.silk_DIV32_16(PacketSize_ms, SilkConstants.MAX_FRAME_LENGTH_MS);
                    psEnc.sCmn.nb_subfr = SilkConstants.MAX_NB_SUBFR;
                    psEnc.sCmn.frame_length = Inlines.silk_SMULBB(20, fs_kHz);
                    psEnc.sCmn.pitch_LPC_win_length = Inlines.silk_SMULBB(SilkConstants.FIND_PITCH_LPC_WIN_MS, fs_kHz);
                    if (psEnc.sCmn.fs_kHz == 8)
                    {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_NB_iCDF.GetPointer();
                    }
                    else {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_iCDF.GetPointer();
                    }
                }
                psEnc.sCmn.PacketSize_ms = PacketSize_ms;
                psEnc.sCmn.TargetRate_bps = 0;         /* trigger new SNR computation */
            }

            /* Set internal sampling frequency */
            Inlines.OpusAssert(fs_kHz == 8 || fs_kHz == 12 || fs_kHz == 16);
            Inlines.OpusAssert(psEnc.sCmn.nb_subfr == 2 || psEnc.sCmn.nb_subfr == 4);
            if (psEnc.sCmn.fs_kHz != fs_kHz)
            {
                /* reset part of the state */
                psEnc.sShape.Reset();
                psEnc.sPrefilt.Reset();
                psEnc.sCmn.sNSQ.Reset();
                psEnc.sCmn.prev_NLSFq_Q15.MemSet(0, SilkConstants.MAX_LPC_ORDER);
                psEnc.sCmn.sLP.In_LP_State.MemSet(0, 2);
                psEnc.sCmn.inputBufIx = 0;
                psEnc.sCmn.nFramesEncoded = 0;
                psEnc.sCmn.TargetRate_bps = 0;     /* trigger new SNR computation */

                /* Initialize non-zero parameters */
                psEnc.sCmn.prevLag = 100;
                psEnc.sCmn.first_frame_after_reset = 1;
                psEnc.sPrefilt.lagPrev = 100;
                psEnc.sShape.LastGainIndex = 10;
                psEnc.sCmn.sNSQ.lagPrev = 100;
                psEnc.sCmn.sNSQ.prev_gain_Q16 = 65536;
                psEnc.sCmn.prevSignalType = SilkConstants.TYPE_NO_VOICE_ACTIVITY;

                psEnc.sCmn.fs_kHz = fs_kHz;
                if (psEnc.sCmn.fs_kHz == 8)
                {
                    if (psEnc.sCmn.nb_subfr == SilkConstants.MAX_NB_SUBFR)
                    {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_NB_iCDF.GetPointer();
                    }
                    else {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_10_ms_NB_iCDF.GetPointer();
                    }
                }
                else {
                    if (psEnc.sCmn.nb_subfr == SilkConstants.MAX_NB_SUBFR)
                    {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_iCDF.GetPointer();
                    }
                    else
                    {
                        psEnc.sCmn.pitch_contour_iCDF = Tables.silk_pitch_contour_10_ms_iCDF.GetPointer();
                    }
                }

                if (psEnc.sCmn.fs_kHz == 8 || psEnc.sCmn.fs_kHz == 12)
                {
                    psEnc.sCmn.predictLPCOrder = SilkConstants.MIN_LPC_ORDER;
                    psEnc.sCmn.psNLSF_CB = Tables.silk_NLSF_CB_NB_MB;
                }
                else
                {
                    psEnc.sCmn.predictLPCOrder = SilkConstants.MAX_LPC_ORDER;
                    psEnc.sCmn.psNLSF_CB = Tables.silk_NLSF_CB_WB;
                }

                psEnc.sCmn.subfr_length = SilkConstants.SUB_FRAME_LENGTH_MS * fs_kHz;
                psEnc.sCmn.frame_length = Inlines.silk_SMULBB(psEnc.sCmn.subfr_length, psEnc.sCmn.nb_subfr);
                psEnc.sCmn.ltp_mem_length = Inlines.silk_SMULBB(SilkConstants.LTP_MEM_LENGTH_MS, fs_kHz);
                psEnc.sCmn.la_pitch = Inlines.silk_SMULBB(SilkConstants.LA_PITCH_MS, fs_kHz);
                psEnc.sCmn.max_pitch_lag = Inlines.silk_SMULBB(18, fs_kHz);

                if (psEnc.sCmn.nb_subfr == SilkConstants.MAX_NB_SUBFR)
                {
                    psEnc.sCmn.pitch_LPC_win_length = Inlines.silk_SMULBB(SilkConstants.FIND_PITCH_LPC_WIN_MS, fs_kHz);
                }
                else
                {
                    psEnc.sCmn.pitch_LPC_win_length = Inlines.silk_SMULBB(SilkConstants.FIND_PITCH_LPC_WIN_MS_2_SF, fs_kHz);
                }

                if (psEnc.sCmn.fs_kHz == 16)
                {
                    psEnc.sCmn.mu_LTP_Q9 = Inlines.SILK_FIX_CONST(TuningParameters.MU_LTP_QUANT_WB, 9);
                    psEnc.sCmn.pitch_lag_low_bits_iCDF = Tables.silk_uniform8_iCDF.GetPointer();
                }
                else if (psEnc.sCmn.fs_kHz == 12)
                {
                    psEnc.sCmn.mu_LTP_Q9 = Inlines.SILK_FIX_CONST(TuningParameters.MU_LTP_QUANT_MB, 9);
                    psEnc.sCmn.pitch_lag_low_bits_iCDF = Tables.silk_uniform6_iCDF.GetPointer();
                }
                else
                {
                    psEnc.sCmn.mu_LTP_Q9 = Inlines.SILK_FIX_CONST(TuningParameters.MU_LTP_QUANT_NB, 9);
                    psEnc.sCmn.pitch_lag_low_bits_iCDF = Tables.silk_uniform4_iCDF.GetPointer();
                }
            }

            /* Check that settings are valid */
            Inlines.OpusAssert((psEnc.sCmn.subfr_length * psEnc.sCmn.nb_subfr) == psEnc.sCmn.frame_length);

            return ret;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="psEncC">I/O</param>
        /// <param name="Complexity">O</param>
        /// <returns></returns>
        public static int silk_setup_complexity(silk_encoder_state psEncC, int Complexity)
        {
            int ret = 0;

            /* Set encoding complexity */
            Inlines.OpusAssert(Complexity >= 0 && Complexity <= 10);
            if (Complexity < 2)
            {
                psEncC.pitchEstimationComplexity = SilkConstants.SILK_PE_MIN_COMPLEX;
                psEncC.pitchEstimationThreshold_Q16 = Inlines.SILK_FIX_CONST(0.8f, 16);
                psEncC.pitchEstimationLPCOrder = 6;
                psEncC.shapingLPCOrder = 8;
                psEncC.la_shape = 3 * psEncC.fs_kHz;
                psEncC.nStatesDelayedDecision = 1;
                psEncC.useInterpolatedNLSFs = 0;
                psEncC.LTPQuantLowComplexity = 1;
                psEncC.NLSF_MSVQ_Survivors = 2;
                psEncC.warping_Q16 = 0;
            }
            else if (Complexity < 4)
            {
                psEncC.pitchEstimationComplexity = SilkConstants.SILK_PE_MID_COMPLEX;
                psEncC.pitchEstimationThreshold_Q16 = Inlines.SILK_FIX_CONST(0.76f, 16);
                psEncC.pitchEstimationLPCOrder = 8;
                psEncC.shapingLPCOrder = 10;
                psEncC.la_shape = 5 * psEncC.fs_kHz;
                psEncC.nStatesDelayedDecision = 1;
                psEncC.useInterpolatedNLSFs = 0;
                psEncC.LTPQuantLowComplexity = 0;
                psEncC.NLSF_MSVQ_Survivors = 4;
                psEncC.warping_Q16 = 0;
            }
            else if (Complexity < 6)
            {
                psEncC.pitchEstimationComplexity = SilkConstants.SILK_PE_MID_COMPLEX;
                psEncC.pitchEstimationThreshold_Q16 = Inlines.SILK_FIX_CONST(0.74f, 16);
                psEncC.pitchEstimationLPCOrder = 10;
                psEncC.shapingLPCOrder = 12;
                psEncC.la_shape = 5 * psEncC.fs_kHz;
                psEncC.nStatesDelayedDecision = 2;
                psEncC.useInterpolatedNLSFs = 1;
                psEncC.LTPQuantLowComplexity = 0;
                psEncC.NLSF_MSVQ_Survivors = 8;
                psEncC.warping_Q16 = psEncC.fs_kHz * Inlines.SILK_FIX_CONST(TuningParameters.WARPING_MULTIPLIER, 16);
            }
            else if (Complexity < 8)
            {
                psEncC.pitchEstimationComplexity = SilkConstants.SILK_PE_MID_COMPLEX;
                psEncC.pitchEstimationThreshold_Q16 = Inlines.SILK_FIX_CONST(0.72f, 16);
                psEncC.pitchEstimationLPCOrder = 12;
                psEncC.shapingLPCOrder = 14;
                psEncC.la_shape = 5 * psEncC.fs_kHz;
                psEncC.nStatesDelayedDecision = 3;
                psEncC.useInterpolatedNLSFs = 1;
                psEncC.LTPQuantLowComplexity = 0;
                psEncC.NLSF_MSVQ_Survivors = 16;
                psEncC.warping_Q16 = psEncC.fs_kHz * Inlines.SILK_FIX_CONST(TuningParameters.WARPING_MULTIPLIER, 16);
            }
            else {
                psEncC.pitchEstimationComplexity = SilkConstants.SILK_PE_MAX_COMPLEX;
                psEncC.pitchEstimationThreshold_Q16 = Inlines.SILK_FIX_CONST(0.7f, 16);
                psEncC.pitchEstimationLPCOrder = 16;
                psEncC.shapingLPCOrder = 16;
                psEncC.la_shape = 5 * psEncC.fs_kHz;
                psEncC.nStatesDelayedDecision = SilkConstants.MAX_DEL_DEC_STATES;
                psEncC.useInterpolatedNLSFs = 1;
                psEncC.LTPQuantLowComplexity = 0;
                psEncC.NLSF_MSVQ_Survivors = 32;
                psEncC.warping_Q16 = psEncC.fs_kHz * Inlines.SILK_FIX_CONST(TuningParameters.WARPING_MULTIPLIER, 16);
            }

            /* Do not allow higher pitch estimation LPC order than predict LPC order */
            psEncC.pitchEstimationLPCOrder = Inlines.silk_min_int(psEncC.pitchEstimationLPCOrder, psEncC.predictLPCOrder);
            psEncC.shapeWinLength = SilkConstants.SUB_FRAME_LENGTH_MS * psEncC.fs_kHz + 2 * psEncC.la_shape;
            psEncC.Complexity = Complexity;

            Inlines.OpusAssert(psEncC.pitchEstimationLPCOrder <= SilkConstants.MAX_FIND_PITCH_LPC_ORDER);
            Inlines.OpusAssert(psEncC.shapingLPCOrder <= SilkConstants.MAX_SHAPE_LPC_ORDER);
            Inlines.OpusAssert(psEncC.nStatesDelayedDecision <= SilkConstants.MAX_DEL_DEC_STATES);
            Inlines.OpusAssert(psEncC.warping_Q16 <= 32767);
            Inlines.OpusAssert(psEncC.la_shape <= SilkConstants.LA_SHAPE_MAX);
            Inlines.OpusAssert(psEncC.shapeWinLength <= SilkConstants.SHAPE_LPC_WIN_MAX);
            Inlines.OpusAssert(psEncC.NLSF_MSVQ_Survivors <= SilkConstants.NLSF_VQ_MAX_SURVIVORS);

            return ret;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="psEncC">I/O</param>
        /// <param name="TargetRate_bps">I</param>
        /// <returns></returns>
        public static int silk_setup_LBRR(silk_encoder_state psEncC, int TargetRate_bps)
        {
            int LBRR_in_previous_packet;
            int ret = SilkError.SILK_NO_ERROR;
            int LBRR_rate_thres_bps;

            LBRR_in_previous_packet = psEncC.LBRR_enabled;
            psEncC.LBRR_enabled = 0;
            if (psEncC.useInBandFEC != 0 && psEncC.PacketLoss_perc > 0)
            {
                if (psEncC.fs_kHz == 8)
                {
                    LBRR_rate_thres_bps = SilkConstants.LBRR_NB_MIN_RATE_BPS;
                }
                else if (psEncC.fs_kHz == 12)
                {
                    LBRR_rate_thres_bps = SilkConstants.LBRR_MB_MIN_RATE_BPS;
                }
                else
                {
                    LBRR_rate_thres_bps = SilkConstants.LBRR_WB_MIN_RATE_BPS;
                }

                LBRR_rate_thres_bps = Inlines.silk_SMULWB(Inlines.silk_MUL(LBRR_rate_thres_bps, 125 - Inlines.silk_min(psEncC.PacketLoss_perc, 25)), Inlines.SILK_FIX_CONST(0.01f, 16));

                if (TargetRate_bps > LBRR_rate_thres_bps)
                {
                    /* Set gain increase for coding LBRR excitation */
                    if (LBRR_in_previous_packet == 0)
                    {
                        /* Previous packet did not have LBRR, and was therefore coded at a higher bitrate */
                        psEncC.LBRR_GainIncreases = 7;
                    }
                    else
                    {
                        psEncC.LBRR_GainIncreases = Inlines.silk_max_int(7 - Inlines.silk_SMULWB((int)psEncC.PacketLoss_perc, Inlines.SILK_FIX_CONST(0.4f, 16)), 2);
                    }
                    psEncC.LBRR_enabled = 1;
                }
            }

            return ret;
        }
    }
}