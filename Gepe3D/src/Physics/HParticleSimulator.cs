
using System;
using OpenTK.Compute.OpenCL;
using OpenTK.Mathematics;

namespace Gepe3D
{
    public class HParticleSimulator
    {
        
        CLCommandQueue queue;
        
        CLEvent @event = new CLEvent();
        
        public readonly int ParticleCount;
        private readonly int cellCount;
        public readonly float[] PosData;
        
        float[] eposData;
        
        
        public static float GRID_CELL_WIDTH = 0.6f; // used for the fluid effect radius
        
        public static float REST_DENSITY = 80f;
        
        public static int
            GridRowsX = 10,
            GridRowsY = 12,
            GridRowsZ = 12;
            
        public static float
            MAX_X = GRID_CELL_WIDTH * GridRowsX,
            MAX_Y = GRID_CELL_WIDTH * GridRowsY,
            MAX_Z = GRID_CELL_WIDTH * GridRowsZ;
        
        private int NUM_ITERATIONS = 1;
        
        public static int
            PHASE_LIQUID = 0,
            PHASE_SOLID = 1;
        
        
        
        private readonly CLKernel
            kPredictPos,        // add external forces then predict positions with euler integration
            kGenNeighbours,     // add particles to bins corresponding to coordinates for easy proximity search
            kDistanceProject,   // project distance constraints (for soft bodies etc)
            kCalcLambdas,       // FLUID - calculate lambda at each particle (scalar for position adjustment)
            kFluidCorrect,        // FLUID - adjust position estimates using lambda values
            kCorrectPredictions,
            kUpdateVel,         // update final position and velocity with bounds collision
            kCalcVorticity,
            kApplyVortVisc,
            kCorrectVel,
            kAssignParticlCells,
            kFindCellsStartAndEnd,
            kSortParticleIDsByCell,
            kSolidCorrect,
            kSolveDistConstraints;
        
        private readonly CLBuffer
            pos,        // positions
            vel,        // velocities
            epos,       // estimated positions
            imass,      // inverse masses
            lambdas,    // scalar for position adjustment
            phase,
            corrections,
            vorticities,
            velCorrect,
            
            sortedParticleIDs,
            cellStartAndEndIDs,
            cellIDsOfParticles,
            numParticlesPerCell,
            particleIDinCell,
            
            numConstraints,
            debugOut;
        
        int[] constraints;
        float[] distances;
        
        private int numDistConstraints;
        
        
        
        
        
        
        public HParticleSimulator(int particleCount)
        {
            
            this.ParticleCount = particleCount;
            this.cellCount = GridRowsX * GridRowsY * GridRowsZ;
            PosData = new float[particleCount * 3];
            eposData = new float[particleCount * 3];
            
            
            ///////////////////
            // Set up OpenCL //
            ///////////////////
            
            // set up context & queue
            CLResultCode result;
            CLPlatform[] platforms;
            CLDevice[] devices;
            result = CL.GetPlatformIds(out platforms);
            result = CL.GetDeviceIds(platforms[0], DeviceType.Gpu, out devices);
            CLContext context = CL.CreateContext(new IntPtr(), 1, devices, new IntPtr(), new IntPtr(), out result);
            this.queue = CL.CreateCommandQueueWithProperties(context, devices[0], new IntPtr(), out result);
            
            // load kernels
            string varDefines            = GenerateDefines(); // combine with other source strings to add common functions
            string commonFuncSource      = CLUtils.LoadSource("res/Kernels/common_funcs.cl");
            string pbdCommonSource       = CLUtils.LoadSource("res/Kernels/pbd_common.cl");
            string fluidProjectSource    = CLUtils.LoadSource("res/Kernels/fluid_project.cl");
            string solidProjectSource    = CLUtils.LoadSource("res/Kernels/solid_project.cl");
            CLProgram pbdProgram         = CLUtils.BuildClProgram(context, devices, varDefines + commonFuncSource + pbdCommonSource   );
            CLProgram fluidProgram       = CLUtils.BuildClProgram(context, devices, varDefines + commonFuncSource + fluidProjectSource);
            CLProgram solidProgram       = CLUtils.BuildClProgram(context, devices, varDefines + commonFuncSource + solidProjectSource);
            
            this.kAssignParticlCells     = CL.CreateKernel( pbdProgram   , "assign_particle_cells"      , out result);
            this.kFindCellsStartAndEnd   = CL.CreateKernel( pbdProgram   , "find_cells_start_and_end"   , out result);
            this.kSortParticleIDsByCell  = CL.CreateKernel( pbdProgram   , "sort_particle_ids_by_cell"  , out result);
            
            this.kPredictPos             = CL.CreateKernel( pbdProgram   , "predict_positions"          , out result);
            this.kCorrectPredictions     = CL.CreateKernel( pbdProgram   , "correct_predictions"        , out result);
            this.kUpdateVel              = CL.CreateKernel( pbdProgram   , "update_velocity"            , out result);
            
            this.kCalcLambdas            = CL.CreateKernel( fluidProgram , "calculate_lambdas"          , out result);
            this.kFluidCorrect           = CL.CreateKernel( fluidProgram , "calc_fluid_corrections"     , out result);
            this.kCalcVorticity          = CL.CreateKernel( fluidProgram , "calculate_vorticities"      , out result);
            this.kApplyVortVisc          = CL.CreateKernel( fluidProgram , "apply_vorticity_viscosity"  , out result);
            this.kCorrectVel             = CL.CreateKernel( fluidProgram , "correct_fluid_vel"          , out result);
            
            this.kSolidCorrect           = CL.CreateKernel( solidProgram , "calc_solid_corrections"          , out result);
            this.kSolveDistConstraints = CL.CreateKernel( solidProgram , "solve_dist_constraint"          , out result);
            
            // create buffers
            this.pos         = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.vel         = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.epos        = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.imass       = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount     , 1);
            this.lambdas     = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount     , 0);
            this.corrections = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.vorticities = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.velCorrect  = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount * 3 , 0);
            this.debugOut    = CLUtils.EnqueueMakeFloatBuffer(context, queue, particleCount     , 0);
            
            this.phase               = CLUtils.EnqueueMakeIntBuffer(context, queue, particleCount , 0);
            this.sortedParticleIDs   = CLUtils.EnqueueMakeIntBuffer(context, queue, particleCount , 0);
            this.cellIDsOfParticles  = CLUtils.EnqueueMakeIntBuffer(context, queue, particleCount , 0);
            this.particleIDinCell    = CLUtils.EnqueueMakeIntBuffer(context, queue, particleCount , 0);
            this.cellStartAndEndIDs  = CLUtils.EnqueueMakeIntBuffer(context, queue, cellCount * 2 , 0);
            this.numParticlesPerCell = CLUtils.EnqueueMakeIntBuffer(context, queue, cellCount     , 0);
            this.numConstraints = CLUtils.EnqueueMakeIntBuffer(context, queue, particleCount, 0);
            
            
            Random rand = new Random();
            float[] rands = new float[particleCount * 3]; // pos data temp
            for (int i = 0; i < rands.Length; i++) {
                
                if (i % 3 == 0) rands[i] = (float) rand.NextDouble() * GridRowsX * GRID_CELL_WIDTH;
                
                if (i % 3 == 1) rands[i] = (float) rand.NextDouble() * GridRowsY * GRID_CELL_WIDTH * 0.7f;
                
                if (i % 3 == 2) rands[i] = (float) rand.NextDouble() * GridRowsZ * GRID_CELL_WIDTH * 0.3f + 0.7f * GridRowsZ * GRID_CELL_WIDTH;
            }
            int[] numConstraintsArray = new int[particleCount];
            
            int solidParticleCount = BallGen.GenBall(
                GridRowsX * GRID_CELL_WIDTH * 0.5f,
                1.1f,
                GridRowsZ * GRID_CELL_WIDTH * 0.3f,
                1, 12, rands, out constraints, out distances, numConstraintsArray);
                
            System.Console.WriteLine(solidParticleCount);
            
            // CubeGenerator.AddCube(0.5f, 2f, 0.5f, 2, 2, 2, 10, 10, 10, rands, out constraints, out distances, numConstraintsArray);
            // System.Console.WriteLine(string.Join(", ", numConstraintsArray));
            
            this.numDistConstraints = distances.Length;
            
            CL.EnqueueWriteBuffer<float>(queue, pos, false, new UIntPtr(), rands, null, out @event);
            CL.EnqueueWriteBuffer<int>(queue, numConstraints, false, new UIntPtr(), numConstraintsArray, null, out @event);
            // System.Console.WriteLine(string.Join(", ", numConstraintsArray));
                
            int[] phaseArray = new int[particleCount];
            for (int i = 0; i< particleCount; i++) {
                // phaseArray[i] = PHASE_SOLID;
                if (i < solidParticleCount) phaseArray[i] = PHASE_SOLID;
                else phaseArray[i] = PHASE_LIQUID;
            }
            CL.EnqueueWriteBuffer<int>(queue, phase, false, new UIntPtr(), phaseArray, null, out @event);
            
            // ensure fills are completed
            CL.Flush(queue);
            CL.Finish(queue);
            
        }
        
        // set pos, phase, colour, constraints, 
        
        
        // TODO: make this more robust
        private string GenerateDefines()
        {
            string defines = "";
            
            defines += "\n" + "#define CELLCOUNT_X " + GridRowsX;
            defines += "\n" + "#define CELLCOUNT_Y " + GridRowsY;
            defines += "\n" + "#define CELLCOUNT_Z " + GridRowsZ;
            defines += "\n" + "#define CELL_WIDTH " + GRID_CELL_WIDTH.ToString("0.0000") + "f";
            defines += "\n" + "#define MAX_X " + MAX_X.ToString("0.0000") + "f";
            defines += "\n" + "#define MAX_Y " + MAX_Y.ToString("0.0000") + "f";
            defines += "\n" + "#define MAX_Z " + MAX_Z.ToString("0.0000") + "f";
            defines += "\n" + "#define KERNEL_SIZE " + GRID_CELL_WIDTH.ToString("0.0000") + "f";
            defines += "\n" + "#define REST_DENSITY " + REST_DENSITY.ToString("0.0000") + "f";
            defines += "\n" + "#define PHASE_LIQUID " + PHASE_LIQUID;
            defines += "\n" + "#define PHASE_SOLID " + PHASE_SOLID;
            defines += "\n";
            
            return defines;
        }
        
        
        
        public void Update(float delta)
        {
            
            CLUtils.EnqueueFillIntBuffer(queue, sortedParticleIDs, 0, ParticleCount);
            CLUtils.EnqueueFillIntBuffer(queue, numParticlesPerCell, 0, cellCount);
            CLUtils.EnqueueFillIntBuffer(queue, numConstraints, 0, ParticleCount);
            CLUtils.EnqueueFillFloatBuffer(queue, corrections, 0, ParticleCount * 3);
            
            CLUtils.EnqueueKernel(queue,  kPredictPos     , ParticleCount, delta, pos, vel, epos);
            
            CLUtils.EnqueueKernel(queue, kAssignParticlCells, ParticleCount, epos, numParticlesPerCell,
                cellIDsOfParticles, particleIDinCell, debugOut);
            CLUtils.EnqueueKernel(queue, kFindCellsStartAndEnd, cellCount, numParticlesPerCell, cellStartAndEndIDs);
            CLUtils.EnqueueKernel(queue, kSortParticleIDsByCell, ParticleCount, particleIDinCell,
                cellStartAndEndIDs, cellIDsOfParticles, sortedParticleIDs, debugOut);
            
            // for (int i = 0; i < NUM_ITERATIONS; i++) {
            
            CLUtils.EnqueueKernel(queue,  kCalcLambdas    , ParticleCount, epos, imass, lambdas, cellIDsOfParticles, cellStartAndEndIDs, sortedParticleIDs, phase, debugOut);
            CLUtils.EnqueueKernel(queue,  kFluidCorrect     , ParticleCount, epos, imass, lambdas, corrections, cellIDsOfParticles, cellStartAndEndIDs, sortedParticleIDs, phase);
            
            CLUtils.EnqueueKernel(queue,  kSolidCorrect     , ParticleCount, epos, imass, corrections, cellIDsOfParticles, cellStartAndEndIDs, sortedParticleIDs, phase);
            
            // CLUtils.EnqueueKernel(queue, kSolveDistConstraints, numDistConstraints, epos, imass, corrections, distConstraintsIDsBuffer, distConstraintsDistancesBuffer, numDistConstraints);
            CLUtils.EnqueueKernel(queue,  kCorrectPredictions   , ParticleCount, pos, epos, corrections, phase, numConstraints);
            
            {
                CL.EnqueueReadBuffer<float>(queue, epos, false, new UIntPtr(), eposData, null, out @event);
                CL.Flush(queue);
                CL.Finish(queue);
                
                for (int i = 0; i < 2; i++)
                    SolveDistConstraints();
                
                CL.EnqueueWriteBuffer<float>(queue, epos, false, new UIntPtr(), eposData, null, out @event);
                CL.Flush(queue);
                CL.Finish(queue);
            }
            
            
            // }
            
            CLUtils.EnqueueKernel(queue,  kUpdateVel      , ParticleCount, delta, pos, vel, epos);
            
            CLUtils.EnqueueKernel(queue,  kCalcVorticity  , ParticleCount, pos, vel, vorticities, cellIDsOfParticles, cellStartAndEndIDs, sortedParticleIDs, phase);
            CLUtils.EnqueueKernel(queue,  kApplyVortVisc  , ParticleCount, pos, vel, vorticities, velCorrect, imass, delta, cellIDsOfParticles, cellStartAndEndIDs, sortedParticleIDs, phase);
            CLUtils.EnqueueKernel(queue,  kCorrectVel     , ParticleCount, vel, velCorrect);
            
            CL.EnqueueReadBuffer<float>(queue, pos, false, new UIntPtr(), PosData, null, out @event);
            
            CL.Flush(queue);
            CL.Finish(queue);
            
        }
        
        
        // jank AF
        private void SolveDistConstraints()
        {
            float stiffness = 0.2f;
            float stiffnessFac = 1 - MathF.Pow( 1 - stiffness, 1f / (float) 2 );
            
            for (int cID = 0; cID < distances.Length; cID++ ) {

                int p1 = constraints[cID * 2 + 0];
                int p2 = constraints[cID * 2 + 1];
                float restDist = distances[cID];
                float imass1 = 1;
                float imass2 = 1;
                if (imass1 == 0 && imass2 == 0) return;

                Vector3 epos1 = new Vector3(eposData[p1 * 3 + 0], eposData[p1 * 3 + 1], eposData[p1 * 3 + 2]);
                Vector3 epos2 = new Vector3(eposData[p2 * 3 + 0], eposData[p2 * 3 + 1], eposData[p2 * 3 + 2]);

                Vector3 dir = epos1 - epos2;
                float displacement = dir.Length - restDist;
                dir.Normalize();

                float w1 = imass1 / (imass1 + imass2);
                float w2 = imass2 / (imass1 + imass2);

                Vector3 correction1 = -w1 * displacement * dir;
                Vector3 correction2 = +w2 * displacement * dir;
                
                eposData[p1 * 3 + 0] += correction1.X * stiffnessFac;
                eposData[p1 * 3 + 1] += correction1.Y * stiffnessFac;
                eposData[p1 * 3 + 2] += correction1.Z * stiffnessFac;
                
                eposData[p2 * 3 + 0] += correction2.X * stiffnessFac;
                eposData[p2 * 3 + 1] += correction2.Y * stiffnessFac;
                eposData[p2 * 3 + 2] += correction2.Z * stiffnessFac;
            }
        }
        
    }
}